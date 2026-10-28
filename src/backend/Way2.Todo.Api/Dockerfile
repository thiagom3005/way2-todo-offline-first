FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Way2.Todo.Api.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
# A imagem aspnet já escuta em 8080 (ASPNETCORE_HTTP_PORTS).
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Way2.Todo.Api.dll"]
