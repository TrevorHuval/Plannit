(function () {
    document.getElementById('inflationPct').addEventListener('input', function () {
        document.getElementById('InflationRate').value = (parseFloat(this.value) || 0) / 100;
    });

    document.getElementById('stdDevPct').addEventListener('input', function () {
        document.getElementById('ReturnStdDev').value = (parseFloat(this.value) || 0) / 100;
    });

    document.querySelectorAll('.returnPct').forEach(function (el) {
        el.addEventListener('input', function () {
            var idx = this.dataset.index;
            document.querySelector('.returnDecimal[data-index="' + idx + '"]').value =
                (parseFloat(this.value) || 0) / 100;
        });
    });

    var eventIndex = parseInt(document.getElementById('initialEventCount').value) || 0;
    var eventsBody = document.getElementById('eventsBody');
    var noEventsMsg = document.getElementById('noEventsMsg');

    document.getElementById('addEventBtn').addEventListener('click', function () {
        if (noEventsMsg) noEventsMsg.style.display = 'none';

        var row = document.createElement('tr');
        row.className = 'event-row';
        row.innerHTML =
            '<td><input name="Events[' + eventIndex + '].Name" class="form-control form-control-sm" placeholder="e.g. Sell house" /></td>' +
            '<td><input name="Events[' + eventIndex + '].Age" class="form-control form-control-sm" type="number" min="0" max="120" value="60" /></td>' +
            '<td><input name="Events[' + eventIndex + '].Amount" class="form-control form-control-sm" type="number" step="1000" value="0" /><small class="text-muted">+income / −expense</small></td>' +
            '<td><input name="Events[' + eventIndex + '].IsRecurring" type="checkbox" class="form-check-input recurring-check" value="true" data-index="' + eventIndex + '" />' +
            '<input type="hidden" name="Events[' + eventIndex + '].IsRecurring" value="false" class="recurring-hidden" data-index="' + eventIndex + '" /></td>' +
            '<td><input name="Events[' + eventIndex + '].EndAge" class="form-control form-control-sm end-age-input" type="number" min="0" max="120" disabled data-index="' + eventIndex + '" /></td>' +
            '<td><button type="button" class="btn btn-sm btn-outline-danger remove-event-btn"><i class="bi bi-trash"></i></button></td>';

        eventsBody.appendChild(row);
        bindEventRow(row);
        eventIndex++;
    });

    function bindEventRow(row) {
        row.querySelector('.remove-event-btn').addEventListener('click', function () {
            row.remove();
            reindexEvents();
        });
        var check = row.querySelector('.recurring-check');
        if (check) {
            check.addEventListener('change', function () {
                var endAgeInput = row.querySelector('.end-age-input');
                endAgeInput.disabled = !this.checked;
                if (!this.checked) endAgeInput.value = '';
            });
        }
    }

    document.querySelectorAll('.event-row').forEach(bindEventRow);

    function reindexEvents() {
        var rows = eventsBody.querySelectorAll('.event-row');
        rows.forEach(function (row, i) {
            row.querySelectorAll('input').forEach(function (input) {
                var name = input.getAttribute('name');
                if (name) input.setAttribute('name', name.replace(/Events\[\d+\]/, 'Events[' + i + ']'));
                if (input.dataset.index !== undefined) input.dataset.index = i;
            });
        });
        eventIndex = rows.length;
        if (noEventsMsg && rows.length === 0) noEventsMsg.style.display = '';
    }

    document.querySelector('form').addEventListener('submit', function () {
        document.querySelectorAll('.recurring-check').forEach(function (check) {
            var idx = check.dataset.index;
            var hidden = document.querySelector('.recurring-hidden[data-index="' + idx + '"]');
            if (check.checked && hidden) hidden.remove();
        });
    });
})();
