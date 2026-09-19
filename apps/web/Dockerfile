FROM node:22-alpine AS deps
WORKDIR /app
COPY package.json package-lock.json* ./
RUN npm ci

FROM node:22-alpine AS builder
WORKDIR /app
ARG NEXT_PUBLIC_API_URL=http://localhost:5000
ENV NEXT_PUBLIC_API_URL=${NEXT_PUBLIC_API_URL}
ARG NEXT_PUBLIC_BUILD_REVISION
ARG RAILWAY_GIT_COMMIT_SHA
ARG GIT_COMMIT_SHA
COPY --from=deps /app/node_modules ./node_modules
COPY . .
RUN NEXT_PUBLIC_BUILD_REVISION="${NEXT_PUBLIC_BUILD_REVISION:-${RAILWAY_GIT_COMMIT_SHA:-${GIT_COMMIT_SHA:-local}}}" npm run build

FROM node:22-alpine AS runner
WORKDIR /app
ENV NODE_ENV=production
ENV PORT=3000
EXPOSE 3000
COPY --from=builder /app/package.json ./package.json
COPY --from=builder /app/node_modules ./node_modules
COPY --from=builder /app/public ./public
COPY --from=builder /app/.next ./.next
CMD ["npm", "run", "start"]
