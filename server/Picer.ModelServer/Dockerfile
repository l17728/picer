FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
EXPOSE 8080 8443
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["Picer.ModelServer.csproj", "."]
RUN dotnet restore "Picer.ModelServer.csproj"
COPY . .
RUN dotnet publish "Picer.ModelServer.csproj" -c Release -o /app/publish --no-restore
FROM runtime AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "Picer.ModelServer.dll"]
