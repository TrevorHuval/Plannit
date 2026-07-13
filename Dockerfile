FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Plannit/Plannit.csproj Plannit/
RUN dotnet restore Plannit/Plannit.csproj

COPY Plannit/ Plannit/
RUN dotnet publish Plannit/Plannit.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN groupadd --system --gid 1654 plannit \
    && useradd --system --uid 1654 --gid plannit --no-create-home plannit \
    && mkdir -p /data /data/keys \
    && chown -R plannit:plannit /data /app

COPY --from=build --chown=plannit:plannit /app/publish .

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:8080
ENV ConnectionStrings__DefaultConnection="DataSource=/data/plannit.db;Cache=Shared"
ENV DataProtection__KeyPath=/data/keys

EXPOSE 8080

USER plannit

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -f http://localhost:8080/healthz || exit 1

ENTRYPOINT ["dotnet", "Plannit.dll"]
