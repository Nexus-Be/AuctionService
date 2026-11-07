namespace Auction.Api.Common;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

public static class ApiValidationProblemFactory
{
    public const string GenericDetail = "One or more validation errors occurred.";
    public const string UnreadableBodyDetail = "The request body is not valid.";

    public static IActionResult Create(ActionContext context)
    {
        var traceId = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
        var errors = CollectErrors(context.ModelState);
        var messages = errors.Values.SelectMany(v => v).ToList();
        var detail = messages.Any(m => m == UnreadableBodyDetail)
            ? UnreadableBodyDetail
            : (messages.Count > 0 ? string.Join(" ", messages) : GenericDetail);

        var problem = new
        {
            type = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
            title = "One or more validation errors occurred.",
            status = StatusCodes.Status400BadRequest,
            errorCode = "VAL_INVALID_INPUT",
            detail,
            errors = errors.Count > 0 ? errors : null,
            traceId,
            timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
        };

        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status400BadRequest,
            ContentTypes = { "application/problem+json" }
        };
    }

    private static Dictionary<string, string[]> CollectErrors(ModelStateDictionary modelState)
    {
        var dict = new Dictionary<string, string[]>();

        foreach (var entry in modelState)
        {
            if (entry.Value is null || entry.Value.Errors.Count == 0)
            {
                continue;
            }

            var errs = entry.Value.Errors
                .Select(error =>
                {
                    var msg = string.IsNullOrWhiteSpace(error.ErrorMessage)
                        ? $"{entry.Key} is invalid."
                        : error.ErrorMessage.Trim();
                    return LeaksInternals(msg) ? UnreadableBodyDetail : msg;
                })
                .Distinct()
                .ToArray();

            if (errs.Length > 0)
            {
                dict[entry.Key] = errs;
            }
        }

        return dict;
    }

    private static bool LeaksInternals(string message) =>
        message.Contains("LineNumber", StringComparison.Ordinal) ||
        message.Contains("BytePositionInLine", StringComparison.Ordinal) ||
        message.Contains("Path: $", StringComparison.Ordinal) ||
        message.Contains("could not be converted to", StringComparison.OrdinalIgnoreCase);
}
