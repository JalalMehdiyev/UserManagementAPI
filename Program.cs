using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Net.Mail;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

var tokenValues = builder.Configuration
    .GetSection("Authentication:Tokens")
    .GetChildren()
    .Select(section => section.Value)
    .ToArray();

if (tokenValues.Length == 0 || tokenValues.Any(string.IsNullOrWhiteSpace))
{
    throw new InvalidOperationException(
        "Configure at least one bearer token using the Authentication:Tokens configuration section.");
}

var validTokens = tokenValues
    .Select(token => token!)
    .ToHashSet(StringComparer.Ordinal);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    var xmlDocumentationFile = Path.Combine(
        AppContext.BaseDirectory,
        $"{typeof(User).Assembly.GetName().Name}.xml");
    if (File.Exists(xmlDocumentationFile))
    {
        options.IncludeXmlComments(xmlDocumentationFile);
    }

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        Description = "Enter a configured bearer token."
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

app.UseMiddleware<RequestLoggingMiddleware>();
app.UseMiddleware<ErrorHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<TokenAuthMiddleware>(validTokens);

var users = new List<User>
{
    new(1, "Aysel Mammadova", "aysel@techhive.com", "HR"),
    new(2, "Rashad Aliyev", "rashad@techhive.com", "IT")
};
var nextId = 3;
var locker = new object();

static NewUser? Normalize(NewUser? input)
{
    if (input is null)
    {
        return null;
    }

    return input with
    {
        Name = input.Name?.Trim(),
        Email = input.Email?.Trim(),
        Department = input.Department?.Trim()
    };
}

static Dictionary<string, string[]> Validate(NewUser? input)
{
    var errors = new Dictionary<string, string[]>();

    if (input is null)
    {
        errors["body"] = ["Request body is required."];
        return errors;
    }

    if (string.IsNullOrWhiteSpace(input.Name))
    {
        errors["name"] = ["Name is required."];
    }
    else if (input.Name.Length > 100)
    {
        errors["name"] = ["Name cannot exceed 100 characters."];
    }

    if (!IsValidEmail(input.Email))
    {
        errors["email"] = ["Enter a valid email address."];
    }

    if (string.IsNullOrWhiteSpace(input.Department))
    {
        errors["department"] = ["Department is required."];
    }
    else if (input.Department.Length > 50)
    {
        errors["department"] = ["Department cannot exceed 50 characters."];
    }

    return errors;
}

static bool IsValidEmail(string? email)
{
    return !string.IsNullOrWhiteSpace(email)
        && !email.Any(char.IsWhiteSpace)
        && new EmailAddressAttribute().IsValid(email)
        && MailAddress.TryCreate(email, out var address)
        && string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase);
}

app.MapGet("/users", () =>
{
    lock (locker)
    {
        return Results.Ok(users.ToList());
    }
})
.WithName("GetUsers")
.WithSummary("Get all users")
.WithDescription("Returns a snapshot of all users in the in-memory collection.")
.Produces<List<User>>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status401Unauthorized);

app.MapGet("/users/{id:int}", (int id) =>
{
    if (id <= 0)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid user ID",
            detail: "ID must be a positive integer.");
    }

    lock (locker)
    {
        var user = users.FirstOrDefault(user => user.Id == id);
        return user is null
            ? Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "User not found",
                detail: $"No user with ID {id} was found.")
            : Results.Ok(user);
    }
})
.WithName("GetUserById")
.WithSummary("Get a user by ID")
.WithDescription("Returns one user when the specified positive ID exists.")
.Produces<User>(StatusCodes.Status200OK)
.ProducesProblem(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status404NotFound)
.Produces(StatusCodes.Status401Unauthorized);

app.MapPost("/users", (NewUser? request) =>
{
    var input = Normalize(request);
    var errors = Validate(input);
    if (errors.Count > 0)
    {
        return Results.ValidationProblem(errors);
    }

    lock (locker)
    {
        var email = input!.Email!;
        if (users.Any(user => user.Email.Equals(email, StringComparison.OrdinalIgnoreCase)))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Email already in use",
                detail: "A user with this email address already exists.");
        }

        var user = new User(nextId++, input.Name!, email, input.Department!);
        users.Add(user);
        return Results.Created($"/users/{user.Id}", user);
    }
})
.WithName("CreateUser")
.WithSummary("Create a user")
.WithDescription("Creates a user after validating and trimming the supplied values. Email addresses must be unique, ignoring case.")
.Produces<User>(StatusCodes.Status201Created)
.ProducesValidationProblem(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status409Conflict)
.Produces(StatusCodes.Status401Unauthorized);

app.MapPut("/users/{id:int}", (int id, NewUser? request) =>
{
    if (id <= 0)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid user ID",
            detail: "ID must be a positive integer.");
    }

    var input = Normalize(request);
    var errors = Validate(input);
    if (errors.Count > 0)
    {
        return Results.ValidationProblem(errors);
    }

    lock (locker)
    {
        var index = users.FindIndex(user => user.Id == id);
        if (index == -1)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "User not found",
                detail: $"No user with ID {id} was found.");
        }

        var email = input!.Email!;
        if (users.Any(user =>
                user.Id != id &&
                user.Email.Equals(email, StringComparison.OrdinalIgnoreCase)))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Email already in use",
                detail: "A user with this email address already exists.");
        }

        users[index] = new User(id, input.Name!, email, input.Department!);
        return Results.Ok(users[index]);
    }
})
.WithName("UpdateUser")
.WithSummary("Update a user")
.WithDescription("Replaces a user's name, email, and department after validation. Email addresses must remain unique, ignoring case.")
.Produces<User>(StatusCodes.Status200OK)
.ProducesValidationProblem(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status404NotFound)
.ProducesProblem(StatusCodes.Status409Conflict)
.Produces(StatusCodes.Status401Unauthorized);

app.MapDelete("/users/{id:int}", (int id) =>
{
    if (id <= 0)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid user ID",
            detail: "ID must be a positive integer.");
    }

    lock (locker)
    {
        var user = users.FirstOrDefault(user => user.Id == id);
        if (user is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "User not found",
                detail: $"No user with ID {id} was found.");
        }

        users.Remove(user);
        return Results.NoContent();
    }
})
.WithName("DeleteUser")
.WithSummary("Delete a user")
.WithDescription("Deletes the user with the specified positive ID.")
.Produces(StatusCodes.Status204NoContent)
.ProducesProblem(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status404NotFound)
.Produces(StatusCodes.Status401Unauthorized);

app.Run();

/// <summary>Represents a user returned by the API.</summary>
sealed record User(int Id, string Name, string Email, string Department);

/// <summary>Values used to create or replace a user.</summary>
sealed record NewUser
{
    /// <summary>The user's display name, up to 100 characters.</summary>
    [Required]
    [StringLength(100)]
    public string? Name { get; init; }

    /// <summary>The user's email address, unique across the collection regardless of case.</summary>
    [Required]
    [EmailAddress]
    public string? Email { get; init; }

    /// <summary>The user's department, up to 50 characters.</summary>
    [Required]
    [StringLength(50)]
    public string? Department { get; init; }
}

sealed class RequestLoggingMiddleware(
    RequestDelegate next,
    ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await next(context);
        }
        finally
        {
            stopwatch.Stop();
            logger.LogInformation(
                "{Method} {Path} responded {StatusCode} in {ElapsedMilliseconds} ms",
                context.Request.Method,
                context.Request.Path,
                context.Response.StatusCode,
                stopwatch.ElapsedMilliseconds);
        }
    }
}

sealed class ErrorHandlingMiddleware(
    RequestDelegate next,
    ILogger<ErrorHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (BadHttpRequestException exception)
        {
            if (context.Response.HasStarted)
            {
                logger.LogWarning(
                    exception,
                    "A bad request occurred after the response started for {Method} {Path}",
                    context.Request.Method,
                    context.Request.Path);
                throw;
            }

            var statusCode = exception.StatusCode is >= 400 and < 500
                ? exception.StatusCode
                : StatusCodes.Status400BadRequest;
            await WriteErrorAsync(
                context,
                statusCode,
                "Bad request",
                "The request could not be processed.");
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Unhandled exception while processing {Method} {Path}",
                context.Request.Method,
                context.Request.Path);

            if (context.Response.HasStarted)
            {
                throw;
            }

            await WriteErrorAsync(
                context,
                StatusCodes.Status500InternalServerError,
                "Internal server error",
                "An unexpected error occurred.");
        }
    }

    private static async Task WriteErrorAsync(
        HttpContext context,
        int statusCode,
        string title,
        string detail)
    {
        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";
        await JsonSerializer.SerializeAsync(context.Response.Body, new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Instance = context.Request.Path
        }, cancellationToken: context.RequestAborted);
    }
}

sealed class TokenAuthMiddleware(RequestDelegate next, HashSet<string> validTokens)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";

        var token = authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? authorization[prefix.Length..].Trim()
            : null;

        if (string.IsNullOrEmpty(token) || !validTokens.Contains(token))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            context.Response.ContentType = "application/problem+json";
            await JsonSerializer.SerializeAsync(context.Response.Body, new ProblemDetails
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Unauthorized",
                Detail = "A valid bearer token is required.",
                Instance = context.Request.Path
            }, cancellationToken: context.RequestAborted);
            return;
        }

        await next(context);
    }
}
