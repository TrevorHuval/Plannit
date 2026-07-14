using Plannit.Models.ViewModels;
using Plannit.Services.Ai;

namespace Plannit.Services;

/// <summary>
/// Serializable state for the multi-file import state machine. The controller
/// persists this across requests (via TempData) while the user works through the
/// pending queue of files that need a confirm/map screen. It carries the results
/// of files that already imported plus the files still awaiting confirmation.
/// </summary>
public class ImportWorkflowState
{
    public int AccountId { get; set; }
    public string AccountName { get; set; } = "";
    public List<ImportResultViewModel> CompletedResults { get; set; } = new();
    public List<PendingImportItemViewModel> PendingQueue { get; set; } = new();
}

/// <summary>Outcome of a workflow step: what the controller should render next.</summary>
public abstract class ImportStep;

/// <summary>Render a confirm/map screen for the next pending file and persist the remaining state.</summary>
public sealed class ShowPendingImportStep : ImportStep
{
    public required string ViewName { get; init; }
    public required object Model { get; init; }
    public required ImportWorkflowState State { get; init; }
}

/// <summary>The queue is drained — render the combined multi-file result page.</summary>
public sealed class FinalizeImportStep : ImportStep
{
    public required MultiImportResultViewModel Result { get; init; }
}

/// <summary>A temp upload went missing; redirect back to the upload screen with a message.</summary>
public sealed class ImportExpiredStep : ImportStep
{
    public required string Message { get; init; }
}

/// <summary>
/// Owns the TempData-driven multi-file import state machine that used to live in
/// TransactionsController. Files that can import without user input (OFX/QFX, CSVs
/// with a saved profile) import immediately; files that need a confirm/map screen
/// (positions CSVs, PDF statements, un-profiled CSVs) are queued and processed one
/// at a time. Auto-categorization and the snapshot nudge run once at the end.
/// The controller stays a thin adapter: it validates input, renders the views this
/// service selects, and shuttles <see cref="ImportWorkflowState"/> through TempData.
/// </summary>
public class ImportWorkflowService
{
    private readonly TransactionService _transactionService;
    private readonly AccountService _accountService;
    private readonly CsvImportService _csvImportService;
    private readonly OfxImportService _ofxImportService;
    private readonly PositionsCsvImportService _positionsCsvImportService;
    private readonly PdfStatementService _pdfStatementService;
    private readonly SnapshotImportService _snapshotImportService;
    private readonly HoldingService _holdingService;
    private readonly CategorizationService _categorizationService;
    private readonly AiSettingsService _aiSettings;
    private readonly SmartCategorizationService _smartCategorization;
    private readonly NotificationService _notificationService;
    private readonly ILogger<ImportWorkflowService> _logger;
    private readonly string _tempUploadPath;

    public ImportWorkflowService(
        TransactionService transactionService,
        AccountService accountService,
        CsvImportService csvImportService,
        OfxImportService ofxImportService,
        PositionsCsvImportService positionsCsvImportService,
        PdfStatementService pdfStatementService,
        SnapshotImportService snapshotImportService,
        HoldingService holdingService,
        CategorizationService categorizationService,
        AiSettingsService aiSettings,
        SmartCategorizationService smartCategorization,
        NotificationService notificationService,
        IWebHostEnvironment env,
        ILogger<ImportWorkflowService> logger)
    {
        _transactionService = transactionService;
        _accountService = accountService;
        _csvImportService = csvImportService;
        _ofxImportService = ofxImportService;
        _positionsCsvImportService = positionsCsvImportService;
        _pdfStatementService = pdfStatementService;
        _snapshotImportService = snapshotImportService;
        _holdingService = holdingService;
        _categorizationService = categorizationService;
        _aiSettings = aiSettings;
        _smartCategorization = smartCategorization;
        _notificationService = notificationService;
        _logger = logger;
        _tempUploadPath = Path.Combine(env.ContentRootPath, "TempUploads");
    }

    private static readonly string[] AllowedTempExtensions = [".csv", ".ofx", ".qfx", ".pdf"];

    /// <summary>
    /// Process a fresh multi-file upload: import what can be imported immediately,
    /// queue what needs a confirm screen, and return the next step for the controller.
    /// </summary>
    public async Task<ImportStep> StartAsync(int accountId, string accountName, IReadOnlyList<Microsoft.AspNetCore.Http.IFormFile> files, bool positionsStatement)
    {
        var completed = new List<ImportResultViewModel>();
        var pendingQueue = new List<PendingImportItemViewModel>();
        Directory.CreateDirectory(_tempUploadPath);

        foreach (var file in files)
        {
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();

            if (ext is ".ofx" or ".qfx")
            {
                using var stream = file.OpenReadStream();
                var result = await _ofxImportService.ImportAsync(stream, accountId, file.FileName);
                result.AccountName = accountName;
                completed.Add(result);
            }
            else if (ext == ".pdf")
            {
                var tempId = await SaveTempFileAsync(file, ".pdf");
                pendingQueue.Add(new PendingImportItemViewModel
                {
                    Kind = "PdfStatement",
                    FileName = file.FileName,
                    TempFileId = tempId,
                    TempFileExtension = ".pdf",
                    AccountId = accountId,
                    AccountName = accountName
                });
            }
            else
            {
                List<string> headers;
                using (var peekStream = file.OpenReadStream())
                {
                    (headers, _) = _csvImportService.ReadPreview(peekStream);
                }

                if (positionsStatement || PositionsCsvImportService.LooksLikePositionsExport(headers))
                {
                    var tempId = await SaveTempFileAsync(file, ".csv");
                    pendingQueue.Add(new PendingImportItemViewModel
                    {
                        Kind = "PositionsCsv",
                        FileName = file.FileName,
                        TempFileId = tempId,
                        TempFileExtension = ".csv",
                        AccountId = accountId,
                        AccountName = accountName
                    });
                    continue;
                }

                var profile = await _csvImportService.GetProfileAsync(accountId);
                if (profile is not null)
                {
                    using var stream = file.OpenReadStream();
                    var result = await _csvImportService.ImportAsync(stream, accountId, file.FileName,
                        profile.DateColumn, profile.DateFormat,
                        profile.AmountColumn, profile.DebitColumn, profile.CreditColumn,
                        profile.DescriptionColumn, profile.InvertAmounts);
                    result.AccountName = accountName;
                    completed.Add(result);
                }
                else
                {
                    var tempId = await SaveTempFileAsync(file, ".csv");
                    pendingQueue.Add(new PendingImportItemViewModel
                    {
                        Kind = "CsvMap",
                        FileName = file.FileName,
                        TempFileId = tempId,
                        TempFileExtension = ".csv",
                        AccountId = accountId,
                        AccountName = accountName
                    });
                }
            }
        }

        if (pendingQueue.Count > 0)
        {
            // Categorize what imported immediately so those rows are settled before the
            // user works through the confirm screens; the final pass re-runs at Finalize.
            if (completed.Count > 0)
            {
                await _categorizationService.ApplyRulesToUncategorizedAsync();
            }

            var state = new ImportWorkflowState
            {
                AccountId = accountId,
                AccountName = accountName,
                CompletedResults = completed,
                PendingQueue = pendingQueue
            };
            return await BuildNextStepAsync(state);
        }

        return await FinalizeAsync(completed, accountName, accountId);
    }

    /// <summary>
    /// Confirm a CSV column mapping: save the profile, import the file, then advance the chain.
    /// The controller validates the model and re-renders MapColumns on failure before calling this.
    /// </summary>
    public async Task<ImportStep> ConfirmCsvMapAsync(ImportMapViewModel model, string accountName, ImportWorkflowState priorState)
    {
        var tempFilePath = GetSafeTempPath(model.TempFileId, ".csv");
        if (tempFilePath is null || !File.Exists(tempFilePath))
        {
            return new ImportExpiredStep { Message = "Upload expired. Please re-upload the file." };
        }

        await _csvImportService.SaveProfileAsync(model.AccountId, model.DateColumn, model.DateFormat,
            model.AmountColumn, model.DebitColumn, model.CreditColumn, model.DescriptionColumn,
            model.InvertAmounts);

        ImportResultViewModel result;
        using (var stream = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read))
        {
            result = await _csvImportService.ImportAsync(stream, model.AccountId, model.FileName,
                model.DateColumn, model.DateFormat, model.AmountColumn, model.DebitColumn,
                model.CreditColumn, model.DescriptionColumn, model.InvertAmounts);
        }

        DeleteTempFile(tempFilePath);
        result.AccountName = accountName;

        return await ContinueAsync(result, model.AccountId, priorState);
    }

    /// <summary>
    /// Confirm a parsed snapshot (positions CSV / PDF statement): upsert the snapshot, then advance the chain.
    /// The controller validates the model and re-renders ConfirmSnapshot on failure before calling this.
    /// </summary>
    public async Task<ImportStep> ConfirmSnapshotAsync(SnapshotConfirmViewModel model, ImportWorkflowState priorState)
    {
        var snapshot = await _snapshotImportService.UpsertSnapshotAsync(model.AccountId, model.AsOfDate, model.Balance);

        var confirmTempPath = GetSafeTempPath(model.TempFileId, model.TempFileExtension);

        // A positions export also populates per-holding detail. The confirm form only
        // round-trips the total balance + date, so re-parse the temp file for the full
        // position rows (quantity/price/cost basis) and upsert holdings at the same date.
        var holdingsUpdated = 0;
        if (model.SourceType == "PositionsCsv" && confirmTempPath is not null && File.Exists(confirmTempPath))
        {
            try
            {
                PositionsImportPreview preview;
                using (var stream = new FileStream(confirmTempPath, FileMode.Open, FileAccess.Read))
                {
                    preview = _positionsCsvImportService.Parse(stream);
                }
                if (preview.Success)
                {
                    holdingsUpdated = await _holdingService.UpsertHoldingsAsync(
                        model.AccountId, model.AsOfDate, preview.Positions);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Holding upsert failed for positions import {FileName}", LogSanitizer.Clean(model.FileName));
            }
        }

        if (confirmTempPath is not null)
        {
            DeleteTempFile(confirmTempPath);
        }

        var result = new ImportResultViewModel
        {
            AccountName = model.AccountName,
            FileName = model.FileName,
            SnapshotOnly = true,
            SnapshotUpdated = snapshot is not null,
            SnapshotBalance = snapshot?.Balance,
            SnapshotDate = snapshot?.Date,
            HoldingsUpdated = holdingsUpdated
        };

        return await ContinueAsync(result, model.AccountId, priorState);
    }

    // Append a just-finished result and either show the next pending file or finalize.
    private async Task<ImportStep> ContinueAsync(ImportResultViewModel newResult, int accountId, ImportWorkflowState priorState)
    {
        priorState.CompletedResults.Add(newResult);

        if (priorState.PendingQueue.Count > 0)
        {
            return await BuildNextStepAsync(priorState);
        }

        return await FinalizeAsync(priorState.CompletedResults, priorState.AccountName, accountId);
    }

    // Pop the head of the pending queue, build its confirm/map screen, and hand back
    // a step carrying the remaining state for the controller to persist.
    private async Task<ImportStep> BuildNextStepAsync(ImportWorkflowState state)
    {
        var item = state.PendingQueue[0];
        var remaining = new ImportWorkflowState
        {
            AccountId = state.AccountId,
            AccountName = state.AccountName,
            CompletedResults = state.CompletedResults,
            PendingQueue = state.PendingQueue.Skip(1).ToList()
        };

        var built = await BuildConfirmViewAsync(item);
        if (built is null)
        {
            return new ImportExpiredStep { Message = $"Upload expired for {item.FileName}. Please re-upload." };
        }

        return new ShowPendingImportStep
        {
            ViewName = built.Value.ViewName,
            Model = built.Value.Model,
            State = remaining
        };
    }

    // Parse a pending file's temp copy and build the confirm/map view model for it.
    // Returns null when the temp file has expired/gone missing.
    private async Task<(string ViewName, object Model)?> BuildConfirmViewAsync(PendingImportItemViewModel item)
    {
        var tempPath = GetSafeTempPath(item.TempFileId, item.TempFileExtension);
        if (tempPath is null || !File.Exists(tempPath))
        {
            return null;
        }

        switch (item.Kind)
        {
            case "PositionsCsv":
            {
                using var stream = new FileStream(tempPath, FileMode.Open, FileAccess.Read);
                var preview = _positionsCsvImportService.Parse(stream);
                return ("ConfirmSnapshot", new SnapshotConfirmViewModel
                {
                    AccountId = item.AccountId,
                    AccountName = item.AccountName,
                    FileName = item.FileName,
                    TempFileId = item.TempFileId,
                    TempFileExtension = item.TempFileExtension,
                    SourceType = "PositionsCsv",
                    AsOfDate = preview.AsOfDate,
                    Balance = preview.Total,
                    BalanceFound = preview.Success,
                    DateFound = preview.DateFromFile,
                    Positions = preview.Positions
                });
            }
            case "PdfStatement":
            {
                using var stream = new FileStream(tempPath, FileMode.Open, FileAccess.Read);
                var preview = _pdfStatementService.Parse(stream);
                return ("ConfirmSnapshot", new SnapshotConfirmViewModel
                {
                    AccountId = item.AccountId,
                    AccountName = item.AccountName,
                    FileName = item.FileName,
                    TempFileId = item.TempFileId,
                    TempFileExtension = item.TempFileExtension,
                    SourceType = "PdfStatement",
                    AsOfDate = preview.AsOfDate ?? DateOnly.FromDateTime(DateTime.Today),
                    Balance = preview.Balance ?? 0,
                    BalanceFound = preview.Balance.HasValue,
                    DateFound = preview.AsOfDate.HasValue,
                    ExtractedText = preview.ExtractedText
                });
            }
            default: // "CsvMap"
            {
                using var stream = new FileStream(tempPath, FileMode.Open, FileAccess.Read);
                var (headers, previewRows) = _csvImportService.ReadPreview(stream);
                var profile = await _csvImportService.GetProfileAsync(item.AccountId);
                var account = await _accountService.GetByIdAsync(item.AccountId);

                var mapVm = new ImportMapViewModel
                {
                    AccountId = item.AccountId,
                    AccountName = item.AccountName,
                    FileName = item.FileName,
                    TempFileId = item.TempFileId,
                    AvailableColumns = headers,
                    PreviewRows = previewRows
                };

                if (profile is not null)
                {
                    mapVm.DateColumn = profile.DateColumn;
                    mapVm.DateFormat = profile.DateFormat;
                    mapVm.AmountColumn = profile.AmountColumn;
                    mapVm.DebitColumn = profile.DebitColumn;
                    mapVm.CreditColumn = profile.CreditColumn;
                    mapVm.DescriptionColumn = profile.DescriptionColumn;
                    mapVm.InvertAmounts = profile.InvertAmounts;
                }
                else
                {
                    mapVm.InvertAmounts = _csvImportService.SuggestInvertAmounts(
                        account is not null && NetWorthService.IsLiability(account.Type), headers, previewRows);
                }

                return ("MapColumns", mapVm);
            }
        }
    }

    // Run the final categorization pass and assemble the combined multi-file result,
    // including the stale-snapshot nudge and AI "smart categorize remaining" prompt.
    private async Task<ImportStep> FinalizeAsync(List<ImportResultViewModel> results, string accountName, int accountId)
    {
        var categorized = 0;
        if (results.Any(r => r.ImportedCount > 0))
        {
            categorized = await _categorizationService.ApplyRulesToUncategorizedAsync();

            // Runs after categorization so category-rule-matched transactions have a CategoryId
            // to compare against their category's historical average.
            foreach (var batchId in results.Where(r => r.ImportBatchId.HasValue).Select(r => r.ImportBatchId!.Value).Distinct())
            {
                try
                {
                    await _notificationService.CheckImportAnomaliesAsync(batchId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Anomaly check failed for import batch {BatchId}", batchId);
                }
            }
        }

        var multiResult = new MultiImportResultViewModel
        {
            AccountName = accountName,
            FileResults = results,
            CategorizedCount = categorized,
            AccountId = accountId
        };

        if (results.Any(r => r.ImportedCount > 0))
        {
            var account = await _accountService.GetByIdAsync(accountId);
            var latestSnapshotDate = account?.Snapshots.MaxBy(s => s.Date)?.Date;
            var newestTxnDate = await _transactionService.GetLatestTransactionDateAsync(accountId);

            if (newestTxnDate.HasValue && (latestSnapshotDate is null || latestSnapshotDate < newestTxnDate))
            {
                multiResult.ShowSnapshotNudge = true;
                multiResult.NudgeAccountId = accountId;
                multiResult.NudgeLatestSnapshotDate = latestSnapshotDate;
                multiResult.NudgeNewestTransactionDate = newestTxnDate.Value;
            }

            if (await _aiSettings.IsConfiguredAsync())
            {
                multiResult.AiConfigured = true;
                multiResult.UncategorizedRemaining = await _smartCategorization.CountUncategorizedAsync(accountId);
            }
        }

        return new FinalizeImportStep { Result = multiResult };
    }

    /// <summary>
    /// Re-read a queued CSV's headers + preview rows so the controller can repopulate
    /// the MapColumns screen after a validation failure. Returns null if the temp file expired.
    /// </summary>
    public (List<string> Headers, List<List<string>> PreviewRows)? ReadCsvPreview(string? tempFileId)
    {
        var tempPath = GetSafeTempPath(tempFileId, ".csv");
        if (tempPath is null || !File.Exists(tempPath)) return null;
        using var stream = new FileStream(tempPath, FileMode.Open, FileAccess.Read);
        return _csvImportService.ReadPreview(stream);
    }

    // Rebuilds the temp path from a parsed Guid and a whitelisted extension so
    // client-posted identifiers can never traverse outside TempUploads.
    private string? GetSafeTempPath(string? tempFileId, string? extension)
    {
        if (!Guid.TryParse(tempFileId, out var id)) return null;
        var ext = string.IsNullOrEmpty(extension) ? ".csv" : extension.ToLowerInvariant();
        if (!AllowedTempExtensions.Contains(ext)) return null;
        return Path.Combine(_tempUploadPath, id.ToString("D") + ext);
    }

    private async Task<string> SaveTempFileAsync(Microsoft.AspNetCore.Http.IFormFile file, string extension)
    {
        var tempId = Guid.NewGuid().ToString();
        var tempPath = Path.Combine(_tempUploadPath, tempId + extension);
        using (var stream = new FileStream(tempPath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }
        return tempId;
    }

    private void DeleteTempFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete temp upload file {File}", LogSanitizer.Clean(Path.GetFileName(path))); }
    }
}
