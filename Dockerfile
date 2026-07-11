FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Plannit/Plannit.csproj Plannit/
RUN dotnet restore Plannit/Plannit.csproj

COPY Plannit/ Plannit/
RUN dotnet publish Plannit/Plannit.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN mkdir -p /data /data/keys

COPY --from=build /app/publish .

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:8080
ENV ConnectionStrings__DefaultConnection="DataSource=/data/plannit.db;Cache=Shared"
ENV DataProtection__KeyPath=/data/keys

EXPOSE 8080

ENTRYPOINT ["dotnet", "Plannit.dll"]
