FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

COPY Plannit/Plannit.csproj Plannit/
RUN dotnet restore Plannit/Plannit.csproj -a $TARGETARCH

COPY Plannit/ Plannit/
RUN dotnet publish Plannit/Plannit.csproj -c Release -a $TARGETARCH -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# The official aspnet image ships a non-root `app` user (UID/GID 1654); reuse it
# rather than creating a second account with the same ids.
RUN mkdir -p /data /data/keys \
    && chown -R app:app /data /app

COPY --from=build --chown=app:app /app/publish .

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:8080
ENV ConnectionStrings__DefaultConnection="DataSource=/data/plannit.db;Cache=Shared"
ENV DataProtection__KeyPath=/data/keys

EXPOSE 8080

USER app

# The runtime image has no curl/wget; probe /healthz over bash's /dev/tcp instead.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD bash -c 'exec 3<>/dev/tcp/127.0.0.1/8080 && printf "GET /healthz HTTP/1.0\r\nHost: localhost\r\n\r\n" >&3 && head -1 <&3 | grep -q " 200 "' || exit 1

ENTRYPOINT ["dotnet", "Plannit.dll"]
