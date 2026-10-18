using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Web.Pages;

/// <summary>
/// A post to a page that has no handler for it is not found. Without this, Razor Pages draws the page anyway, and a
/// page whose data is loaded by its handlers fails with a 500 on the empty model. Runs after authorization, so a
/// signed-out visitor is still sent to sign in first. The error pages are left alone: a refused post is shown by
/// running it again through <c>/Error</c> with its method unchanged, and that page must draw with the status it was
/// given (a 403 stays a 403).
/// </summary>
public sealed class UnknownHandlerFilter : IAsyncPageFilter
{
    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context)
    {
        return Task.CompletedTask;
    }

    public Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var request = context.HttpContext;
        var shownAsError = request.Features.Get<IStatusCodeReExecuteFeature>() is not null
            || request.Features.Get<IExceptionHandlerFeature>() is not null;
        if (context.HandlerMethod is null && !HttpMethods.IsGet(request.Request.Method) && !HttpMethods.IsHead(request.Request.Method) && !shownAsError)
        {
            context.Result = new NotFoundResult();

            return Task.CompletedTask;
        }

        return next();
    }
}
