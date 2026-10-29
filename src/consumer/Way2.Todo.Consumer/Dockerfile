FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Way2.Todo.Consumer.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
# Volume do log de eventos; criado com dono = usuário da aplicação (o volume nomeado herda).
RUN mkdir -p /data && chown "$APP_UID" /data
# A imagem aspnet já escuta em 8080 em todas as interfaces (ASPNETCORE_HTTP_PORTS).
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Way2.Todo.Consumer.dll"]
