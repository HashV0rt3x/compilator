FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

COPY Compilator.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /publish

# Runtime image with all judge compilers
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

RUN apt-get update && apt-get install -y --no-install-recommends \
    g++ \
    gcc \
    default-jdk \
    python3 \
    golang-go \
  && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /publish .

RUN mkdir -p /opt/judge/testcases

ENV ASPNETCORE_URLS=http://+:8080
ENV Judge__TestCasesBasePath=/opt/judge/testcases
ENV Judge__MaxParallelContainers=4

EXPOSE 8080

ENTRYPOINT ["dotnet", "Compilator.dll"]
