# Plannit

Personal financial planning web app — net worth tracking, statement imports (CSV/OFX/PDF), expense analytics, and retirement projections.

## Stack

- .NET 10, ASP.NET Core MVC
- SQLite via EF Core
- ASP.NET Core Identity
- Bootstrap 5, Chart.js

## Running locally

```bash
cd Plannit
dotnet run
```

App runs at http://localhost:5103

## Migrations

```bash
dotnet ef migrations add <Name>
dotnet ef database update
```

## Deployment

```bash
docker build -t plannit .
docker run -d -p 8080:8080 -v plannit-data:/data plannit
```

See `DEPLOY.md` for bare-metal setup and PostgreSQL migration notes.
