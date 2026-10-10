# ---- 文档站构建（npm ci + lockfile 保证依赖一致；构建上下文为 docs/ 目录）----
FROM node:22-alpine AS build
WORKDIR /docs
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
ENV NEXT_TELEMETRY_DISABLED=1
RUN npm run build

# ---- 运行（Next.js standalone 最小产物）----
FROM node:22-alpine
WORKDIR /docs
ENV NODE_ENV=production \
    HOSTNAME=0.0.0.0 \
    PORT=3000 \
    NEXT_TELEMETRY_DISABLED=1
COPY --from=build /docs/.next/standalone ./
COPY --from=build /docs/.next/static ./.next/static
COPY --from=build /docs/public ./public
EXPOSE 3000
CMD ["node", "server.js"]
