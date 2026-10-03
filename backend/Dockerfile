# ---- 前端构建（pnpm + lockfile 保证依赖一致）----
FROM node:22-alpine AS web
WORKDIR /web
COPY frontend/package.json frontend/pnpm-lock.yaml ./
RUN npm i -g pnpm@9 --no-fund --no-audit \
    && pnpm install --frozen-lockfile
COPY frontend/ ./
RUN npm run build

# ---- 后端构建（构建上下文为仓库根目录：docker build -f backend/Dockerfile .）----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY backend/ .
RUN dotnet publish src/Ark.Api/Ark.Api.csproj -c Release -o /app/publish

# ---- 运行 ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
COPY --from=web /web/dist ./wwwroot

# Program.cs 以 ContentRootPath/../../../data 定位数据目录 → 容器内即 /data
# HOME 指向 /data，让 DataProtection 密钥（~/.aspnet/DataProtection-Keys）随数据卷持久化，
# 否则容器重建后新密钥无法解密 ark.db 中已加密的连接密码
ENV ASPNETCORE_HTTP_PORTS=8080 \
    HOME=/data
RUN mkdir -p /data
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "Ark.Api.dll"]
