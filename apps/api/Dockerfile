FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["Taslim.Api.csproj", "./"]
RUN dotnet restore "Taslim.Api.csproj"
COPY . .
RUN dotnet publish "Taslim.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
RUN apt-get update \
    && apt-get install -y --no-install-recommends libfontconfig1 libfreetype6 libharfbuzz0b \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --system app \
    && useradd --system --gid app --home-dir /app --no-create-home app
COPY --from=build /app/publish .
RUN chown -R app:app /app
USER app
ENTRYPOINT ["dotnet", "Taslim.Api.dll"]
