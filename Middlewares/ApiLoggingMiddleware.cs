using System.Diagnostics;
using Serilog.Events;
using DicomSCP.Services;

namespace DicomSCP.Middlewares;

public class ApiLoggingMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await _next(context);
            sw.Stop();
        }
        catch (Exception ex)
        {
            sw.Stop();
            DicomLogger.Error("Api", ex,
                "[API] 请求失败 - {Method} {Path}",
                context.Request.Method,
                context.Request.Path);
            throw;
        }
        finally
        {
            // 记录请求的详细信息
            var statusCode = context.Response.StatusCode;
            var level = statusCode >= 500 ? LogEventLevel.Error :
                       statusCode >= 400 ? LogEventLevel.Warning :
                       LogEventLevel.Information;

            if (level == LogEventLevel.Error)
            {
                DicomLogger.Error("Api", null,
                    "[API] 请求详情 - {Method} {Path} - {StatusCode} - {ElapsedMs:0.0000}ms",
                    context.Request.Method,
                    context.Request.Path,
                    statusCode,
                    sw.Elapsed.TotalMilliseconds);
            }
            else if (level == LogEventLevel.Warning)
            {
                DicomLogger.Warning("Api",
                    "[API] 请求详情 - {Method} {Path} - {StatusCode} - {ElapsedMs:0.0000}ms",
                    context.Request.Method,
                    context.Request.Path,
                    statusCode,
                    sw.Elapsed.TotalMilliseconds);
            }
            else
            {
                DicomLogger.Information("Api",
                    "[API] 请求详情 - {Method} {Path} - {StatusCode} - {ElapsedMs:0.0000}ms",
                    context.Request.Method,
                    context.Request.Path,
                    statusCode,
                    sw.Elapsed.TotalMilliseconds);
            }
        }
    }
} 