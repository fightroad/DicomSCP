using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Microsoft.Extensions.Options;
using DicomSCP.Configuration;
using Microsoft.Extensions.Logging;

namespace DicomSCP.Services;

public interface IStoreSCU
{
    Task<bool> VerifyConnectionAsync(string remoteName, CancellationToken cancellationToken = default);
    Task SendFolderAsync(string folderPath, string remoteName, CancellationToken cancellationToken = default);
    Task SendFilesAsync(IEnumerable<string> filePaths, string remoteName, CancellationToken cancellationToken = default);
    Task SendFileAsync(string filePath, string remoteName, CancellationToken cancellationToken = default);
}

public class StoreSCU : IStoreSCU
{
    private readonly QueryRetrieveConfig _config;
    private readonly string _callingAE;
    private readonly int _localPort;
    private readonly ILoggerFactory _loggerFactory;

    public StoreSCU(
        IOptions<QueryRetrieveConfig> config,
        ILoggerFactory loggerFactory)
    {
        _config = config.Value;
        _callingAE = _config.LocalAeTitle;
        _localPort = _config.LocalPort;
        _loggerFactory = loggerFactory;
    }

    private IDicomClient CreateClient(RemoteNode node)
    {
        var client = DicomClientFactory.Create(
            node.HostName, 
            node.Port, 
            false, 
            _callingAE, 
            node.AeTitle);

        client.NegotiateAsyncOps();
        return client;
    }

    private RemoteNode GetRemoteNode(string remoteName)
    {
        var node = _config.RemoteNodes?.FirstOrDefault(n => n.Name.Equals(remoteName, StringComparison.OrdinalIgnoreCase));
        if (node == null)
        {
            throw new ArgumentException($"未找到名为 {remoteName} 的远程节点配置");
        }
        return node;
    }

    public async Task SendFileAsync(string filePath, string remoteName, CancellationToken cancellationToken = default)
    {
        try
        {
            var node = GetRemoteNode(remoteName);
            DicomLogger.Information("StoreSCU", "开始发送 DICOM 文件 - 文件: {FilePath}, 目标: {Name} ({Host}:{Port} {AE})",
                filePath, node.Name, node.HostName, node.Port, node.AeTitle);

            // 创建客户端
            var client = CreateClient(node);

            // 加载 DICOM 文件
            var file = await DicomFile.OpenAsync(filePath);

            // 创建 C-STORE 请求
            var request = new DicomCStoreRequest(file);

            // 注册回调
            request.OnResponseReceived += (req, response) =>
            {
                DicomLogger.Information("StoreSCU", "收到存储响应 - 状态: {Status}", response.Status);
            };

            // 发送请求
            await client.AddRequestAsync(request);
            await client.SendAsync(cancellationToken);

            DicomLogger.Information("StoreSCU", "DICOM 文件发送完成 - 文件: {FilePath}", filePath);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("StoreSCU", ex, "发送 DICOM 文件失败 - 文件: {FilePath}", filePath);
            throw;
        }
    }

    public async Task SendFilesAsync(IEnumerable<string> filePaths, string remoteName, CancellationToken cancellationToken = default)
    {
        try
        {
            var node = GetRemoteNode(remoteName);
            DicomLogger.Information("StoreSCU", "开始批量发送 DICOM 文件 - 文件数: {Count}, 目标: {Name} ({Host}:{Port} {AE})",
                filePaths.Count(), node.Name, node.HostName, node.Port, node.AeTitle);

            // 创建客户端
            var client = CreateClient(node);

            // 添加所有文件的请求
            foreach (var filePath in filePaths)
            {
                try
                {
                    var file = await DicomFile.OpenAsync(filePath);
                    var request = new DicomCStoreRequest(file);

                    request.OnResponseReceived += (req, response) =>
                    {
                        DicomLogger.Information("StoreSCU", "收到存储响应 - 文件: {FilePath}, 状态: {Status}",
                            filePath, response.Status);
                    };

                    await client.AddRequestAsync(request);
                }
                catch (Exception ex)
                {
                    DicomLogger.Error("StoreSCU", ex, "添加文件到发送队列失败 - 文件: {FilePath}", filePath);
                    continue;
                }
            }

            // 发送所有请求
            await client.SendAsync(cancellationToken);

            DicomLogger.Information("StoreSCU", "批量 DICOM 文件发送完成 - 总文件数: {Count}", filePaths.Count());
        }
        catch (Exception ex)
        {
            DicomLogger.Error("StoreSCU", ex, "批量发送 DICOM 文件失败");
            throw;
        }
    }

    public async Task SendFolderAsync(string folderPath, string remoteName, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Directory.Exists(folderPath))
            {
                throw new DirectoryNotFoundException($"文件夹不存在: {folderPath}");
            }

            var files = Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories)
                .Where(f => IsDicomFile(f));

            await SendFilesAsync(files, remoteName, cancellationToken);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("StoreSCU", ex, "发送文件夹失败 - 文件夹: {FolderPath}", folderPath);
            throw;
        }
    }

    public async Task<bool> VerifyConnectionAsync(string remoteName, CancellationToken cancellationToken = default)
    {
        try
        {
            var node = GetRemoteNode(remoteName);
            DicomLogger.Information("StoreSCU", "开始验证连接 - 目标: {Name} ({Host}:{Port} {AE})",
                node.Name, node.HostName, node.Port, node.AeTitle);

            var client = CreateClient(node);

            var verified = false;
            var request = new DicomCEchoRequest();
            
            request.OnResponseReceived += (req, response) =>
            {
                verified = response.Status == DicomStatus.Success;
                DicomLogger.Information("StoreSCU", "收到 C-ECHO 响应 - 状态: {Status}", response.Status);
            };

            await client.AddRequestAsync(request);
            await client.SendAsync(cancellationToken);

            DicomLogger.Information("StoreSCU", "连接验证完成 - 结果: {Result}", verified);
            return verified;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("StoreSCU", ex, "连接验证失败");
            return false;
        }
    }

    private bool IsDicomFile(string filePath)
    {
        try
        {
            using var fileStream = File.OpenRead(filePath);
            var buffer = new byte[132];
            if (fileStream.Read(buffer, 0, 132) < 132)
            {
                return false;
            }

            // 检查 DICOM 文件头
            return buffer[128] == 'D' &&
                   buffer[129] == 'I' &&
                   buffer[130] == 'C' &&
                   buffer[131] == 'M';
        }
        catch
        {
            return false;
        }
    }
} 