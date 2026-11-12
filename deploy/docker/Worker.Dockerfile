# Hosts.Worker host image. Build context is the repository root (see deploy/compose/docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0.403-noble AS build
WORKDIR /repo
COPY global.json ./
COPY src ./src
RUN dotnet publish src/Hosts.Worker/Hosts.Worker.csproj -c Release -r linux-x64 --self-contained false -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-noble AS runtime
# ML.NET's glibc MKL imports need the libiomp5.so alias to the LLVM OpenMP runtime.
RUN apt-get update -qq \
    && apt-get install -y -qq --no-install-recommends libomp5-18 wget \
    && ln -s libomp.so.5 /usr/lib/x86_64-linux-gnu/libiomp5.so \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "Hosts.Worker.dll"]
