namespace Auction.Api.Common;

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
        var messages = CollectMessages(context.ModelState);
        var detail = messages.Any(LeaksInternals) ? UnreadableBodyDetail : string.Join(" ", messages);

        var problem = new
        {
            type = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
            title = "Validation error",
            status = StatusCodes.Status400BadRequest,
            errorCode = "VAL_INVALID_INPUT",
            detail,
            traceId
        };

        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status400BadRequest,
            ContentTypes = { "application/problem+json" }
        };
    }

    private static List<string> CollectMessages(ModelStateDictionary modelState)
    {
        var messages = new List<string>();

        foreach (var entry in modelState)
        {
            if (entry.Value is null)
            {
                continue;
            }

            foreach (var error in entry.Value.Errors)
            {
                messages.Add(string.IsNullOrWhiteSpace(error.ErrorMessage)
                    ? $"{entry.Key} is invalid."
                    : error.ErrorMessage.Trim());
            }
        }

        return messages.Count == 0 ? new List<string> { GenericDetail } : messages.Distinct().ToList();
    }

    private static bool LeaksInternals(string message) =>
        message.Contains("LineNumber", StringComparison.Ordinal) ||
        message.Contains("BytePositionInLine", StringComparison.Ordinal) ||
        message.Contains("Path: $", StringComparison.Ordinal) ||
        message.Contains("could not be converted to", StringComparison.OrdinalIgnoreCase);
}
