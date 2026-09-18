# Taslim.ai

Taslim.ai is an AI super-platform focused on making professional AI capabilities simple, fast, high-quality, and fair to use.

## Product principles

- Simple: users choose what they want to create; Taslim hides technical complexity.
- Fast: long-running AI jobs run asynchronously and notify users when ready.
- Professional: outputs should be production-quality, not raw model responses.
- Fair: no hidden charges, no automatic top-ups by default, and unfair or failed charges are refunded/restored.
- Multilingual: English, Arabic, and Kurdish Sorani from the foundation, including RTL support.
- Provider-independent: all external AI services are accessed through Taslim's AI Core/provider adapters.

## Initial product areas

- Taslim Chat
- Media: Images, Movies, Voice, Music
- Business: Company, Sales, Finance, Planning, Research, HR
- Marketing: Social Media, Advertising, Branding, Content, Design, Product Catalog, Campaigns
- Personal: Photos, Documents, Learning, Health, Travel, Lifestyle
- Education: Study, Teaching, Research, Courses
- Development: Learn, Build, Code, Debug, Projects

## Repository structure (planned)

```text
/apps
  /web         Next.js web app
  /api         ASP.NET Core API
/packages
  /contracts   Shared API contracts / generated types
/docs          Architecture and product specifications
```

## First build milestone

1. Repository and solution foundation
2. Web shell and navigation
3. API health endpoint
4. PostgreSQL connection
5. Authentication foundation
6. Taslim Chat first vertical slice

This repository is the source of truth for Taslim.ai development.
