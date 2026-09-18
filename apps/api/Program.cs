using Microsoft.EntityFrameworkCore;
using Taslim.Api.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? builder.Configuration["DATABASE_URL"]
    ?? "Host=localhost;Port=5432;Database=taslim;Username=taslim;Password=change-me";

builder.Services.AddDbContext<TaslimDbContext>(options => options.UseNpgsql(connectionString));

var allowedOrigins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:3000"];
builder.Services.AddCors(options => options.AddPolicy("Frontend", policy =>
    policy.WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("Frontend");
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "Taslim API" }))
    .WithName("Health")
    .WithTags("System");
app.MapControllers();

app.Run();

public partial class Program;
