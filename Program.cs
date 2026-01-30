using FellowOakDicom;
using FellowOakDicom.Imaging.NativeCodec;
using Serilog;
using Serilog.Events;
using DicomSCP.Configuration;
using DicomSCP.Services;
using DicomSCP.Data;
using Microsoft.OpenApi.Models;
using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Rewrite;

var builder = WebApplication.CreateBuilder(args);

// 配置控制台（跨平台支持）
if (Environment.UserInteractive)
{
    try
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows平台禁用快速编辑模式
            var handle = ConsoleHelper.GetStdHandle(-10);
            if (handle != IntPtr.Zero && ConsoleHelper.GetConsoleMode(handle, out uint mode))
            {
                mode &= ~(uint)(0x0040 | 0x0010);
                ConsoleHelper.SetConsoleMode(handle, mode);
            }
        }
        else
        {
            // Unix/Linux/MacOS 平台设置
            Console.TreatControlCAsInput = true;
        }
    }
    catch
    {
        // 忽略控制台配置错误
    }
}

// 注册编码提供程序
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

// 注册 DICOM 编码
DicomEncoding.RegisterEncoding("GB2312", "GB2312");

// 大文件上传：显式设置 Kestrel 与 Form 限制（全局配置有时不生效，这里强制生效）
var maxBodySizeBytes = builder.Configuration.GetValue<long>("Kestrel:Limits:MaxRequestBodySize", 524288000);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maxBodySizeBytes;
});
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxBodySizeBytes;
});

// 取配置
var settings = builder.Configuration.GetSection("DicomSettings").Get<DicomSettings>() 
    ?? new DicomSettings();
// 获取 Swagger 配置
var swaggerSettings = builder.Configuration.GetSection("Swagger").Get<SwaggerSettings>()
    ?? new SwaggerSettings();

// 配置日志
var logSettings = builder.Configuration
    .GetSection("Logging")
    .Get<LogSettings>() ?? new LogSettings();

// 初始化DICOM日志
DicomLogger.Initialize(logSettings);

// 初始化数据库日志
BaseRepository.ConfigureLogging(logSettings);

// 初始化API日志
ApiLoggingMiddleware.ConfigureLogging(logSettings);

// 配置框架日志
var logConfig = new LoggerConfiguration()
    .MinimumLevel.Warning()  // 只记录警告以上的日志
    .Filter.ByExcluding(e => 
        e.Properties.ContainsKey("SourceContext") && 
        e.Properties["SourceContext"].ToString().Contains("FellowOakDicom.Network") &&
        (e.MessageTemplate.Text.Contains("No accepted presentation context found") ||
         e.MessageTemplate.Text.Contains("Study Root Query/Retrieve Information Model - FIND") ||
         e.MessageTemplate.Text.Contains("Patient Root Query/Retrieve Information Model - FIND") ||
         e.MessageTemplate.Text.Contains("Storage Commitment Push Model SOP Class") ||
         e.MessageTemplate.Text.Contains("Modality Performed Procedure Step") ||
         e.MessageTemplate.Text.Contains("Basic Grayscale Print Management Meta") ||
         e.MessageTemplate.Text.Contains("Basic Color Print Management Meta") ||
         e.MessageTemplate.Text.Contains("Verification SOP Class") ||
         e.MessageTemplate.Text.Contains("rejected association") ||
         e.MessageTemplate.Text.Contains("Association received")))
    .WriteTo.Logger(lc => lc
        .WriteTo.Console(
            outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:l}{NewLine}",
            restrictedToMinimumLevel: LogEventLevel.Warning
        )
    );

Log.Logger = logConfig.CreateLogger();
builder.Host.UseSerilog();

// 添加日志服务
builder.Services.AddLogging(loggingBuilder =>
    loggingBuilder.AddSerilog(dispose: true));

// 添加服务
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// 配置 Swagger
if (swaggerSettings.Enabled)
{
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc(swaggerSettings.Version, new OpenApiInfo 
        { 
            Title = swaggerSettings.Title,
            Version = swaggerSettings.Version,
            Description = swaggerSettings.Description
        });
    });
}

// DICOM服务注册
builder.Services
    .AddFellowOakDicom()
    .AddTranscoderManager<NativeTranscoderManager>();

builder.Services.AddSingleton<DicomRepository>();
builder.Services.AddSingleton<DicomServer>();
builder.Services.AddSingleton<WorklistRepository>();
builder.Services.AddSingleton<IStoreSCU, StoreSCU>();
builder.Services.AddSingleton<IPrintSCU, PrintSCU>();

// 确保配置服务正确注册
builder.Services.Configure<DicomSettings>(builder.Configuration.GetSection("DicomSettings"));
builder.Services.Configure<QueryRetrieveConfig>(builder.Configuration.GetSection("QueryRetrieveConfig"));
builder.Services.AddScoped<IQueryRetrieveSCU, QueryRetrieveSCU>();
// 注册 Swagger 配置
builder.Services.Configure<SwaggerSettings>(builder.Configuration.GetSection("Swagger"));

builder.Services.AddAuthentication("CustomAuth")
    .AddCookie("CustomAuth", options =>
    {
        options.Cookie.Name = "auth";
        options.LoginPath = "/login.html";
        // cookies过期时间
        options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
        options.SlidingExpiration = false;  // 禁用默认的滑动过期
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.Path = "/";
        //启用最大有效期后下发的是请求的时间戳，不打开就是请求的时间戳+30分钟。
        //如果设置的大于30分钟，则每次请求都会更新过期时间，导致过期时间不准确。
        //options.Cookie.MaxAge = TimeSpan.FromHours(12);

        // 添加验证票据过期处理
        options.Events = new CookieAuthenticationEvents
        {
            OnRedirectToLogin = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                }
                context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            },
            // 修改验证处理
            OnValidatePrincipal = async context =>
            {
                // 检查是否已过期
                if (context.Properties?.ExpiresUtc <= DateTimeOffset.UtcNow)
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync("CustomAuth");
                    return;
                }

                // 如果不是 status 接口，手动更新过期时间
                if (!context.HttpContext.Request.Path.StartsWithSegments("/api/dicom/status") && 
                    context.Properties is not null)
                {
                    context.Properties.ExpiresUtc = DateTimeOffset.UtcNow.Add(options.ExpireTimeSpan);
                    context.ShouldRenew = true;
                }
            }
        };
    });

// 添加授权但不设置默认策略
builder.Services.AddAuthorization();

// 配置转发头
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | 
                              Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    // 清除默认网络，否则会因为安全检查而被忽略
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// 在 ConfigureServices 部分添加
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", 
        builder =>
        {
            builder.AllowAnyOrigin()
                   .AllowAnyMethod()
                   .AllowAnyHeader();
        });
});

// 配置 URL 重写规则
var rewriteOptions = new RewriteOptions()
    //将dicomviewer路径下非直接文件的访问重写到dicomviewer/index.html上，解决spa单应用路由问题
    .AddRewrite(
        @"^dicomviewer/(?!.*\.(js|css|png|jpe?g|gif|ico|svg|woff2?|ttf|otf|eot|map|json|mp[34]|webm|mkv|avi|mov|pdf|docx?|xlsx?|pptx?|zip|rar|tar|gz|7z|ts|sh|bat|py|xml|ya?ml|ini|wasm|aac)).*$", 
        "/dicomviewer/index.html", 
        skipRemainingRules: true
    );

var app = builder.Build();

// 初始化服务提供者
DicomServiceProvider.Initialize(app.Services);

// 获取服务
var dicomRepository = app.Services.GetRequiredService<DicomRepository>();

// 配置 DICOM
DicomSetupBuilder.UseServiceProvider(app.Services);

CStoreSCP.Configure(settings, dicomRepository);

// 启动 DICOM 服务器
var dicomServer = app.Services.GetRequiredService<DicomServer>();
await dicomServer.StartAsync();
app.Lifetime.ApplicationStopping.Register(() => dicomServer.StopAsync().GetAwaiter().GetResult());

// 优化线程池 - 基于CPU核心数
int processorCount = Environment.ProcessorCount;
ThreadPool.SetMinThreads(processorCount * 4, processorCount * 2);    // 最小线程数
ThreadPool.SetMaxThreads(processorCount * 8, processorCount * 4);    // 最大线程数

// 1. 转发头中间件（最先）
app.UseForwardedHeaders();

// 2. API 日志中间件
app.UseMiddleware<ApiLoggingMiddleware>();

// 3. URL 重写（在静态文件之前）
app.UseRewriter(rewriteOptions);

// 4. 静态文件处理
app.UseDefaultFiles(new DefaultFilesOptions
{
    DefaultFileNames = new List<string> { "index.html", "login.html" }
});
app.UseStaticFiles();

// 5. Swagger
if (swaggerSettings.Enabled)
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// 6. 路由和认证
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseCors("AllowAll");  // CORS 应该在这里

// 7. API 认证中间件
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value?.ToLower();
    
    // 只检查 API 路径的认证
    if (path?.StartsWith("/api/") == true && 
        !path.StartsWith("/api/auth/login") && 
        !context.User.Identity?.IsAuthenticated == true)
    {
        context.Response.StatusCode = 401;
        return;
    }
    
    await next();
});

// 8. 控制器
app.MapControllers();

// 9. 根路径处理
app.MapGet("/", context =>
{
    if (!context.User.Identity?.IsAuthenticated == true)
    {
        context.Response.Redirect("/login.html");
    }
    else
    {
        context.Response.Redirect("/index.html");
    }
    return Task.CompletedTask;
});

// 启动完成后输出管理系统地址
app.Lifetime.ApplicationStarted.Register(() =>
{
    // 从配置读取地址，没有就用默认值
    var httpUrl = builder.Configuration["Kestrel:Endpoints:Http:Url"] ?? "http://localhost:5000";
    
    // 提取端口号
    var port = "5000";
    if (httpUrl.Contains(":"))
    {
        var parts = httpUrl.Split(':');
        if (parts.Length >= 3)
        {
            port = parts[parts.Length - 1];
        }
    }
    
    // 获取本地IP地址
    string? localIp = null;
    try
    {
        var hostEntry = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
        foreach (var ip in hostEntry.AddressList)
        {
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                localIp = ip.ToString();
                break;
            }
        }
    }
    catch
    {
        // 忽略获取IP地址的错误
    }
    
    Console.WriteLine("═══════════════════════════════════════════════════════════");
    Console.WriteLine("   DICOM SCP 服务器启动成功！");
    Console.WriteLine("═══════════════════════════════════════════════════════════");
    Console.WriteLine($"   监听地址: {httpUrl}");
    
    if (!string.IsNullOrEmpty(localIp))
    {
        Console.WriteLine($"   管理系统地址: http://{localIp}:{port}");
    }
    else
    {
        Console.WriteLine($"   管理系统地址: {httpUrl}");
    }
    
    Console.WriteLine("═══════════════════════════════════════════════════════════");
    Console.WriteLine();
});

app.Run(); 

// Windows控制台API定义
internal static class ConsoleHelper
{
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
} 