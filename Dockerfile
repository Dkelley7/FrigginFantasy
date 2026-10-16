FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY NuGet.Config ./
COPY src/SundayRivalry/SundayRivalry.csproj src/SundayRivalry/
RUN dotnet restore src/SundayRivalry --configfile NuGet.Config
COPY src/SundayRivalry/ src/SundayRivalry/
RUN dotnet publish src/SundayRivalry -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080
ENV DataDirectory=/data
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "SundayRivalry.dll"]
