FROM node:22-alpine@sha256:0a7108bf6c7bf5de370ffb1a3ed6be93d405b43ff159f681a8d18c0e2bc2e402 AS deps
WORKDIR /app
COPY package.json package-lock.json* ./
RUN npm ci

FROM node:22-alpine@sha256:0a7108bf6c7bf5de370ffb1a3ed6be93d405b43ff159f681a8d18c0e2bc2e402 AS builder
WORKDIR /app
ARG NEXT_PUBLIC_API_URL=https://taslim-api-production.up.railway.app
RUN test -n "$NEXT_PUBLIC_API_URL" && case "$NEXT_PUBLIC_API_URL" in https://*) ;; *) echo "NEXT_PUBLIC_API_URL must be an HTTPS URL for production builds" >&2; exit 1 ;; esac
ENV NEXT_PUBLIC_API_URL=${NEXT_PUBLIC_API_URL}
COPY --from=deps /app/node_modules ./node_modules
COPY . .
RUN npm run build

FROM node:22-alpine@sha256:0a7108bf6c7bf5de370ffb1a3ed6be93d405b43ff159f681a8d18c0e2bc2e402 AS runner
WORKDIR /app
ENV NODE_ENV=production
ENV PORT=3000
EXPOSE 3000
RUN addgroup -S app && adduser -S app -G app
COPY --from=builder /app/package.json ./package.json
COPY --from=builder /app/node_modules ./node_modules
COPY --from=builder /app/public ./public
COPY --from=builder /app/.next ./.next
RUN chown -R app:app /app
USER app
CMD ["npm", "run", "start"]
