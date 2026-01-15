using System.Text;
using FellowOakDicom.Network;
using Microsoft.Extensions.Options;
using DicomSCP.Configuration;
using DicomSCP.Data;


namespace DicomSCP.Services;

public sealed class DicomServer : IDisposable
{
    private readonly DicomSettings _settings;
    private readonly IConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;
    private readonly DicomRepository _repository;
    
    private IDicomServer? _storeScp;
    private IDicomServer? _worklistScp;
    private IDicomServer? _qrScp;
    private IDicomServer? _printScp;
    private bool _disposed;
    private readonly Dictionary<string, IDicomServer> _servers;

    public DicomServer(
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        IOptions<DicomSettings> settings,
        DicomRepository repository)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _servers = new Dictionary<string, IDicomServer>();
    }

    public bool IsRunning => _storeScp != null || _worklistScp != null || _qrScp != null || _printScp != null;

    public async Task StartAsync()
    {
        if (IsRunning)
        {
            DicomLogger.Warning("DICOM", "DICOM服务器已在运行中");
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                EnsureStorageDirectory();
                StartDicomServices();
            });
        }
        catch (Exception ex)
        {
            DicomLogger.Error("DICOM", ex, "DICOM服务启动失败 - AET: {AeTitle}", _settings.AeTitle);
            _storeScp?.Dispose();
            _worklistScp?.Dispose();
            _qrScp?.Dispose();
            _printScp?.Dispose();
            _storeScp = _worklistScp = null;
            _qrScp = null;
            _printScp = null;
            throw;
        }
    }

    private void EnsureStorageDirectory()
    {
        if (!Directory.Exists(_settings.StoragePath))
        {
            try
            {
                Directory.CreateDirectory(_settings.StoragePath);
                DicomLogger.Information("DICOM", "创建存储目录: {Path}", _settings.StoragePath);
            }
            catch (Exception ex)
            {
                DicomLogger.Error("DICOM", ex, "创建存储目录失败: {Path}", _settings.StoragePath);
                throw;
            }
        }
    }

    private void StartDicomServices()
    {
        try
        {
            Console.WriteLine("═══════════════════════════════════════════════════════════");
            // 配置存储服务
            CStoreSCP.Configure(_settings, _repository);

            // 配置工作列表服务
            WorklistSCP.Configure(
                _settings,
                _configuration,
                _repository);

            try
            {
                // 启动存储服务
                _storeScp = DicomServerFactory.Create<CStoreSCP>(
                    _settings.StoreSCPPort,
                    null,
                    Encoding.UTF8,
                    _loggerFactory.CreateLogger<CStoreSCP>());

                DicomLogger.Information("DICOM", "接收归档服务已启动 - AET: {AeTitle}, 端口: {Port}", 
                    _settings.AeTitle, _settings.StoreSCPPort);
            }
            catch (Exception ex)
            {
                DicomLogger.Error("DICOM", ex, "启动C-STORE服务失败");
                throw;
            }

            try
            {
                // 启动工作列表服务
                _worklistScp = DicomServerFactory.Create<WorklistSCP>(
                    _settings.WorklistSCP.Port,
                    null,
                    Encoding.UTF8,
                    _loggerFactory.CreateLogger<WorklistSCP>());

                DicomLogger.Information("DICOM", "病人列表服务已启动 - AET: {AeTitle}, 端口: {Port}", 
                    _settings.WorklistSCP.AeTitle, _settings.WorklistSCP.Port);
            }
            catch (Exception ex)
            {
                DicomLogger.Error("DICOM", ex, "启动Worklist服务失败");
                throw;
            }

            try
            {
                // 启动QR服务
                _qrScp = DicomServerFactory.Create<QRSCP>(
                    _settings.QRSCP.Port,
                    null,
                    Encoding.UTF8,
                    _loggerFactory.CreateLogger<QRSCP>(),
                    _repository);

                DicomLogger.Information("DICOM", "查询检索服务已启动 - AET: {AeTitle}, 端口: {Port}", 
                    _settings.QRSCP.AeTitle, _settings.QRSCP.Port);
            }
            catch (Exception ex)
            {
                DicomLogger.Error("DICOM", ex, "启动QR服务失败");
                throw;
            }

            try
            {
                // 启动打印服务
                _printScp = DicomServerFactory.Create<PrintSCP>(
                    _settings.PrintSCP.Port,
                    null,
                    Encoding.UTF8,
                    _loggerFactory.CreateLogger<PrintSCP>());

                DicomLogger.Information("DICOM", "胶片打印服务已启动 - AET: {AeTitle}, 端口: {Port}", 
                    _settings.PrintSCP.AeTitle, _settings.PrintSCP.Port);
            }
            catch (Exception ex)
            {
                DicomLogger.Error("DICOM", ex, "启动打印服务失败");
                throw;
            }
        }
        catch (Exception ex)
        {
            DicomLogger.Error("DICOM", ex, "DICOM服务启动失败 - AET: {AeTitle}", _settings.AeTitle);
            _storeScp?.Dispose();
            _worklistScp?.Dispose();
            _qrScp?.Dispose();
            _printScp?.Dispose();
            _storeScp = _worklistScp = null;
            _qrScp = null;
            _printScp = null;
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                DicomLogger.Information("DICOM", "正在停止DICOM服务...");
                _storeScp?.Dispose();
                _worklistScp?.Dispose();
                _qrScp?.Dispose();
                _printScp?.Dispose();
                _storeScp = _worklistScp = null;
                _qrScp = null;
                _printScp = null;
            });
            DicomLogger.Information("DICOM", "DICOM服务已停止...");
        }
        catch (Exception ex)
        {
            DicomLogger.Error("DICOM", ex, "停止DICOM服务时发生错误!");
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _storeScp?.Dispose();
        _worklistScp?.Dispose();
        _qrScp?.Dispose();
        _printScp?.Dispose();
        _disposed = true;
    }

    public async Task RestartAllServices()
    {
        try
        {
            DicomLogger.Information("DICOM", "正在重启所有DICOM服务...");
            await StopAsync();
            await StartAsync();
            DicomLogger.Information("DICOM", "所有DICOM服务重启完成");
        }
        catch (Exception ex)
        {
            DicomLogger.Error("DICOM", ex, "重启DICOM服务失败");
            throw;
        }
    }

    public class ServiceStatus
    {
        public bool IsRunning { get; set; }
        public required ServicesStatus Services { get; set; }
    }

    public class ServicesStatus
    {
        public bool StoreScp { get; set; }
        public bool WorklistScp { get; set; }
        public bool QrScp { get; set; }
        public bool PrintScp { get; set; }
    }

    public ServiceStatus GetServicesStatus()
    {
        return new ServiceStatus
        {
            IsRunning = IsRunning,
            Services = new ServicesStatus
            {
                StoreScp = _storeScp != null,
                WorklistScp = _worklistScp != null,
                QrScp = _qrScp != null,
                PrintScp = _printScp != null
            }
        };
    }
} 