using Dapper;
using DicomSCP.Models;
using FellowOakDicom;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Data;
using Microsoft.Extensions.Options;
using DicomSCP.Configuration;
using AsyncKeyedLock;

namespace DicomSCP.Data;

public class DicomRepository : BaseRepository, IDisposable
{
    private readonly ConcurrentQueue<(DicomDataset Dataset, string FilePath)> _dataQueue = new();
    private readonly AsyncNonKeyedLocker _processSemaphore = new();
    private readonly Timer _processTimer;
    private readonly int _batchSize;
    private readonly TimeSpan _maxWaitTime = TimeSpan.FromSeconds(10);
    private readonly TimeSpan _minWaitTime = TimeSpan.FromSeconds(2);
    private readonly Stopwatch _performanceTimer = new();
    private DateTime _lastProcessTime = DateTime.Now;
    private bool _initialized;
    private bool _disposed;
    private readonly DicomSettings _settings;

    private static class SqlQueries
    {
        public const string InsertPatient = @"
            INSERT OR IGNORE INTO Patients 
            (PatientId, PatientName, PatientBirthDate, PatientSex, CreateTime)
            VALUES (@PatientId, @PatientName, @PatientBirthDate, @PatientSex, @CreateTime)";

        public const string InsertStudy = @"
            INSERT OR IGNORE INTO Studies 
            (StudyInstanceUid, PatientId, StudyDate, StudyTime, StudyDescription, 
             AccessionNumber, Modality, InstitutionName, CreateTime)
            VALUES (@StudyInstanceUid, @PatientId, @StudyDate, @StudyTime, @StudyDescription, 
                    @AccessionNumber, @Modality, @InstitutionName, @CreateTime)";

        public const string InsertSeries = @"
            INSERT OR IGNORE INTO Series 
            (
                SeriesInstanceUid, 
                StudyInstanceUid, 
                Modality, 
                SeriesNumber, 
                SeriesDescription,
                SliceThickness,
                SeriesDate,        
                CreateTime
            )
            VALUES 
            (
                @SeriesInstanceUid, 
                @StudyInstanceUid, 
                @Modality, 
                @SeriesNumber, 
                @SeriesDescription,
                @SliceThickness,
                @SeriesDate,
                @CreateTime
            )";

        public const string InsertInstance = @"
            INSERT OR IGNORE INTO Instances (
                SopInstanceUid, SeriesInstanceUid, SopClassUid, InstanceNumber, FilePath,
                Columns, Rows, PhotometricInterpretation, BitsAllocated, BitsStored,
                PixelRepresentation, SamplesPerPixel, PixelSpacing, HighBit,
                ImageOrientationPatient, ImagePositionPatient, FrameOfReferenceUID,
                ImageType, WindowCenter, WindowWidth, CreateTime
            ) VALUES (
                @SopInstanceUid, @SeriesInstanceUid, @SopClassUid, @InstanceNumber, @FilePath,
                @Columns, @Rows, @PhotometricInterpretation, @BitsAllocated, @BitsStored,
                @PixelRepresentation, @SamplesPerPixel, @PixelSpacing, @HighBit,
                @ImageOrientationPatient, @ImagePositionPatient, @FrameOfReferenceUID,
                @ImageType, @WindowCenter, @WindowWidth, @CreateTime
            )";

        public const string CreateWorklistTable = @"
            CREATE TABLE IF NOT EXISTS Worklist (
                WorklistId TEXT PRIMARY KEY,
                AccessionNumber TEXT,
                PatientId TEXT,
                PatientName TEXT,
                PatientBirthDate TEXT,
                PatientSex TEXT,
                StudyInstanceUid TEXT,
                StudyDescription TEXT,
                Modality TEXT,
                ScheduledAET TEXT,
                ScheduledDateTime TEXT,
                ScheduledStationName TEXT,
                ScheduledProcedureStepID TEXT,
                ScheduledProcedureStepDescription TEXT,
                RequestedProcedureID TEXT,
                RequestedProcedureDescription TEXT,
                ReferringPhysicianName TEXT,
                Status TEXT DEFAULT 'SCHEDULED',
                BodyPartExamined TEXT,
                ReasonForRequest TEXT,
                CreateTime DATETIME DEFAULT CURRENT_TIMESTAMP,
                UpdateTime DATETIME DEFAULT CURRENT_TIMESTAMP
            )";

        public const string QueryWorklist = @"
            SELECT * FROM Worklist 
            WHERE (@PatientId IS NULL OR PatientId LIKE @PatientId)
            AND (@AccessionNumber IS NULL OR AccessionNumber LIKE @AccessionNumber)
            AND (@ScheduledDateTime IS NULL OR ScheduledDateTime LIKE @ScheduledDateTime)
            AND (@Modality IS NULL OR Modality = @Modality)
            AND (@ScheduledStationName IS NULL OR ScheduledStationName = @ScheduledStationName)
            AND Status = 'SCHEDULED'";

        public const string CreatePatientsTable = @"
            CREATE TABLE IF NOT EXISTS Patients (
                PatientId TEXT PRIMARY KEY,
                PatientName TEXT,
                PatientBirthDate TEXT,
                PatientSex TEXT,
                CreateTime DATETIME
            )";

        public const string CreateStudiesTable = @"
            CREATE TABLE IF NOT EXISTS Studies (
                StudyInstanceUid TEXT PRIMARY KEY,
                PatientId TEXT,
                StudyDate TEXT,
                StudyTime TEXT,
                StudyDescription TEXT,
                AccessionNumber TEXT,
                Modality TEXT,
                InstitutionName TEXT,
                CreateTime DATETIME,
                FOREIGN KEY(PatientId) REFERENCES Patients(PatientId)
            )";

        public const string CreateSeriesTable = @"
            CREATE TABLE IF NOT EXISTS Series (
                SeriesInstanceUid TEXT PRIMARY KEY,
                StudyInstanceUid TEXT,
                Modality TEXT,
                SeriesNumber TEXT,
                SeriesDescription TEXT,
                SliceThickness TEXT,
                SeriesDate TEXT,
                CreateTime DATETIME,
                FOREIGN KEY(StudyInstanceUid) REFERENCES Studies(StudyInstanceUid)
            )";

        public const string CreateInstancesTable = @"
            CREATE TABLE IF NOT EXISTS Instances (
                SopInstanceUid TEXT PRIMARY KEY,
                SeriesInstanceUid TEXT,
                SopClassUid TEXT,
                InstanceNumber TEXT,
                FilePath TEXT,
                Columns INTEGER,
                Rows INTEGER,
                PhotometricInterpretation TEXT,
                BitsAllocated INTEGER,
                BitsStored INTEGER,
                PixelRepresentation INTEGER,
                SamplesPerPixel INTEGER,
                PixelSpacing TEXT,
                HighBit INTEGER,
                ImageOrientationPatient TEXT,
                ImagePositionPatient TEXT,
                FrameOfReferenceUID TEXT,
                ImageType TEXT,
                WindowCenter TEXT,
                WindowWidth TEXT,
                CreateTime DATETIME,
                FOREIGN KEY(SeriesInstanceUid) REFERENCES Series(SeriesInstanceUid)
            )";

        public const string CreateUsersTable = @"
            CREATE TABLE IF NOT EXISTS Users (
                Username TEXT PRIMARY KEY,
                Password TEXT NOT NULL
            )";

        public const string InitializeAdminUser = @"
            INSERT OR IGNORE INTO Users (Username, Password) 
            VALUES ('admin', 'jGl25bVBBBW96Qi9Te4V37Fnqchz/Eu4qB9vKrRIqRg=')";

        public const string CreatePrintJobsTable = @"
            CREATE TABLE IF NOT EXISTS PrintJobs (
                JobId TEXT PRIMARY KEY,
                FilmSessionId TEXT,
                FilmBoxId TEXT,
                CallingAE TEXT,
                Status TEXT,
                ErrorMessage TEXT,

                -- Film Session 参数
                NumberOfCopies INTEGER DEFAULT 1,
                PrintPriority TEXT DEFAULT 'LOW',
                MediumType TEXT DEFAULT 'BLUE FILM',
                FilmDestination TEXT DEFAULT 'MAGAZINE',

                -- Film Box 参数
                PrintInColor INTEGER DEFAULT 0,
                FilmOrientation TEXT DEFAULT 'PORTRAIT',
                FilmSizeID TEXT DEFAULT '8INX10IN',
                ImageDisplayFormat TEXT DEFAULT 'STANDARD\1,1',
                MagnificationType TEXT DEFAULT 'REPLICATE',
                SmoothingType TEXT DEFAULT 'MEDIUM',
                BorderDensity TEXT DEFAULT 'BLACK',
                EmptyImageDensity TEXT DEFAULT 'BLACK',
                Trim TEXT DEFAULT 'NO',

                -- 图像信息
                ImagePath TEXT,

                -- 研究信息
                StudyInstanceUID TEXT,

                -- 时间戳
                CreateTime DATETIME DEFAULT CURRENT_TIMESTAMP,
                UpdateTime DATETIME DEFAULT CURRENT_TIMESTAMP
            )";
    }

    public DicomRepository(IConfiguration configuration, ILogger<DicomRepository> logger, IOptions<DicomSettings> settings)
        : base(configuration.GetConnectionString("DicomDb") ?? throw new ArgumentException("Missing DicomDb connection string"), logger)
    {
        _settings = settings.Value;
        _batchSize = configuration.GetValue<int>("DicomSettings:BatchSize", 50);

        // 初始化数据库
        Task.Run(async () =>
        {
            try
            {
                await InitializeDatabase();
            }
            catch (Exception ex)
            {
                LogError(ex, "初始化数据库失败");
            }
        }).GetAwaiter().GetResult();

        _processTimer = new Timer(async _ => await ProcessQueueAsync(), null, _minWaitTime, _minWaitTime);
    }

    private async Task ProcessQueueAsync()
    {
        if (_dataQueue.IsEmpty) return;

        var queueSize = _dataQueue.Count;
        var waitTime = DateTime.Now - _lastProcessTime;

        // 修改处理时机判断
        if (queueSize >= _batchSize || // 队列达到批处理大小
            (queueSize > 0 && waitTime >= _maxWaitTime) || // 等待超过10秒就处理
            (queueSize >= 5 && waitTime >= _minWaitTime))  // 只要有5条且等待超过2秒就处理
        {
            await ProcessBatchWithRetryAsync();
        }
    }

    private async Task ProcessBatchWithRetryAsync()
    {
        using var semLock = await _processSemaphore.LockOrNullAsync(TimeSpan.FromSeconds(1));

        if (semLock is null)
        {
            return;
        }

        List<(DicomDataset Dataset, string FilePath)> batchItems = new();

        try
        {
            _performanceTimer.Restart();
            var batchSize = Math.Min(_dataQueue.Count, _batchSize);
            
            // 一次性收集批处理数据
            while (batchItems.Count < batchSize && _dataQueue.TryDequeue(out var item))
            {
                batchItems.Add(item);
            }

            if (batchItems.Count == 0) return;

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            try
            {
                var now = DateTime.Now;
                var patients = new List<Patient>();
                var studies = new List<Study>();
                var series = new List<Series>();
                var instances = new List<Instance>();

                // 预分配容量以提高性能
                patients.Capacity = batchItems.Count;
                studies.Capacity = batchItems.Count;
                series.Capacity = batchItems.Count;
                instances.Capacity = batchItems.Count;

                foreach (var (dataset, filePath) in batchItems)
                {
                    try
                    {
                        ExtractDicomData(dataset, filePath, now, patients, studies, series, instances);
                    }
                    catch (Exception ex)
                    {
                        // 记录单条数据处理错误，但继续处理其他数据
                        LogError(ex, "处理DICOM数据失败 - 文件: {FilePath}", filePath);
                        continue;
                    }
                }

                if (patients.Count == 0 && studies.Count == 0 && series.Count == 0 && instances.Count == 0)
                {
                    LogWarning("批处理中没有有效数据");
                    return;
                }

                // 批量插入数据，使用 INSERT OR IGNORE
                var insertedPatients = await connection.ExecuteAsync(SqlQueries.InsertPatient, patients, transaction);
                var insertedStudies = await connection.ExecuteAsync(SqlQueries.InsertStudy, studies, transaction);
                var insertedSeries = await connection.ExecuteAsync(SqlQueries.InsertSeries, series, transaction);
                var insertedInstances = await connection.ExecuteAsync(SqlQueries.InsertInstance, instances, transaction);

                await transaction.CommitAsync();

                _performanceTimer.Stop();
                _lastProcessTime = DateTime.Now;

                LogInformation(
                    "批量处理完成 - 总数: {Count}, 新增: P={Patients}, S={Studies}, Se={Series}, I={Instances}, 耗时: {Time}ms, 队列剩余: {Remaining}", 
                    batchItems.Count,
                    insertedPatients,
                    insertedStudies,
                    insertedSeries,
                    insertedInstances,
                    _performanceTimer.ElapsedMilliseconds,
                    _dataQueue.Count);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                LogError(ex, "数据库操作失败 - 批次大小: {Count}", batchItems.Count);
                
                // 记录失败的数据，但不重新入队
                foreach (var (dataset, filePath) in batchItems)
                {
                    try
                    {
                        var sopInstanceUid = dataset.GetSingleValueOrDefault<string>(DicomTag.SOPInstanceUID, "Unknown");
                        var studyInstanceUid = dataset.GetSingleValueOrDefault<string>(DicomTag.StudyInstanceUID, "Unknown");
                        var seriesInstanceUid = dataset.GetSingleValueOrDefault<string>(DicomTag.SeriesInstanceUID, "Unknown");
                        
                        LogInformation("数据入库失败 - 文件: {FilePath}, Study: {Study}, Series: {Series}, Instance: {Instance}", 
                            filePath, studyInstanceUid, seriesInstanceUid, sopInstanceUid);
                    }
                    catch
                    {
                        LogInformation("数据入库失败且无法获取标识信息 - 文件: {FilePath}", filePath);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogError(ex, "处理批次时发生异常");
        }
    }

    private async Task InitializeDatabase()
    {
        if (_initialized) return;

        // 确保数据库目录存在
        var dbPath = Path.GetDirectoryName(_connectionString.Replace("Data Source=", "").Trim());
        if (!string.IsNullOrEmpty(dbPath))
        {
            Directory.CreateDirectory(dbPath);
        }

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var transaction = await connection.BeginTransactionAsync();

        // 检查是否已存在表
        var tableExists = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Studies'");

        try
        {
            // 创建所有表
            await connection.ExecuteAsync(SqlQueries.CreatePatientsTable, transaction: transaction);
            await connection.ExecuteAsync(SqlQueries.CreateStudiesTable, transaction: transaction);
            await connection.ExecuteAsync(SqlQueries.CreateSeriesTable, transaction: transaction);
            await connection.ExecuteAsync(SqlQueries.CreateInstancesTable, transaction: transaction);
            await connection.ExecuteAsync(SqlQueries.CreateWorklistTable, transaction: transaction);
            await connection.ExecuteAsync(SqlQueries.CreateUsersTable, transaction: transaction);
            await connection.ExecuteAsync(SqlQueries.InitializeAdminUser, transaction: transaction);
            
            // 创建打印任务表
            await connection.ExecuteAsync(SqlQueries.CreatePrintJobsTable, transaction: transaction);

            await transaction.CommitAsync();
            _initialized = true;
            if (tableExists == 0)
            {
                LogInformation("数据库表首次初始化完成");
            }
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            LogError(ex, "初始化数据库表失败");
            throw;
        }
    }

    private void ExtractDicomData(
        DicomDataset dataset, 
        string filePath, 
        DateTime now,
        List<Patient> patients,
        List<Study> studies,
        List<Series> series,
        List<Instance> instances)
    {
        var patientId = dataset.GetSingleValue<string>(DicomTag.PatientID);
        var studyInstanceUid = dataset.GetSingleValue<string>(DicomTag.StudyInstanceUID);
        var seriesInstanceUid = dataset.GetSingleValue<string>(DicomTag.SeriesInstanceUID);

        patients.Add(new Patient
        {
            PatientId = patientId,
            PatientName = dataset.GetSingleValueOrDefault<string>(DicomTag.PatientName, string.Empty),
            PatientBirthDate = dataset.GetSingleValueOrDefault<string>(DicomTag.PatientBirthDate, string.Empty),
            PatientSex = dataset.GetSingleValueOrDefault<string>(DicomTag.PatientSex, string.Empty),
            CreateTime = now
        });

        studies.Add(new Study
        {
            StudyInstanceUid = studyInstanceUid,
            PatientId = patientId,
            StudyDate = dataset.GetSingleValueOrDefault<string>(DicomTag.StudyDate, string.Empty),
            StudyTime = dataset.GetSingleValueOrDefault<string>(DicomTag.StudyTime, string.Empty),
            StudyDescription = dataset.GetSingleValueOrDefault<string>(DicomTag.StudyDescription, string.Empty),
            AccessionNumber = dataset.GetSingleValueOrDefault<string>(DicomTag.AccessionNumber, string.Empty),
            Modality = GetStudyModality(dataset),
            InstitutionName = dataset.GetSingleValueOrDefault<string>(DicomTag.InstitutionName, string.Empty),
            CreateTime = now
        });

        series.Add(new Series
        {
            SeriesInstanceUid = seriesInstanceUid,
            StudyInstanceUid = studyInstanceUid,
            Modality = dataset.GetSingleValueOrDefault<string>(DicomTag.Modality, string.Empty),
            SeriesNumber = dataset.GetSingleValueOrDefault<string>(DicomTag.SeriesNumber, string.Empty),
            SeriesDescription = dataset.GetSingleValueOrDefault<string>(DicomTag.SeriesDescription, string.Empty),
            SliceThickness = dataset.GetSingleValueOrDefault<string>(DicomTag.SliceThickness, string.Empty),
            SeriesDate = dataset.GetSingleValueOrDefault<string>(DicomTag.SeriesDate, string.Empty),
            CreateTime = now
        });

        instances.Add(new Instance
        {
            SopInstanceUid = dataset.GetSingleValue<string>(DicomTag.SOPInstanceUID),
            SeriesInstanceUid = seriesInstanceUid,
            SopClassUid = dataset.GetSingleValue<string>(DicomTag.SOPClassUID),
            InstanceNumber = dataset.GetSingleValueOrDefault<string>(DicomTag.InstanceNumber, string.Empty),
            FilePath = filePath,
            Columns = dataset.GetSingleValueOrDefault<int>(DicomTag.Columns, 0),
            Rows = dataset.GetSingleValueOrDefault<int>(DicomTag.Rows, 0),
            PhotometricInterpretation = dataset.GetSingleValueOrDefault<string>(DicomTag.PhotometricInterpretation, string.Empty),
            BitsAllocated = dataset.GetSingleValueOrDefault<int>(DicomTag.BitsAllocated, 0),
            BitsStored = dataset.GetSingleValueOrDefault<int>(DicomTag.BitsStored, 0),
            PixelRepresentation = dataset.GetSingleValueOrDefault<int>(DicomTag.PixelRepresentation, 0),
            SamplesPerPixel = dataset.GetSingleValueOrDefault<int>(DicomTag.SamplesPerPixel, 0),
            
            // 处理可能是单值或多值的标签
            PixelSpacing = dataset.Contains(DicomTag.PixelSpacing)
                ? (dataset.GetValueCount(DicomTag.PixelSpacing) > 1
                    ? string.Join("\\", dataset.GetValues<decimal>(DicomTag.PixelSpacing))
                    : dataset.GetSingleValueOrDefault<decimal>(DicomTag.PixelSpacing, 0).ToString())
                : string.Empty,
            
            HighBit = dataset.GetSingleValueOrDefault<int>(DicomTag.HighBit, 0),
            
            ImageOrientationPatient = dataset.Contains(DicomTag.ImageOrientationPatient)
                ? (dataset.GetValueCount(DicomTag.ImageOrientationPatient) > 1
                    ? string.Join("\\", dataset.GetValues<decimal>(DicomTag.ImageOrientationPatient))
                    : dataset.GetSingleValueOrDefault<decimal>(DicomTag.ImageOrientationPatient, 0).ToString())
                : string.Empty,
            
            ImagePositionPatient = dataset.Contains(DicomTag.ImagePositionPatient)
                ? (dataset.GetValueCount(DicomTag.ImagePositionPatient) > 1
                    ? string.Join("\\", dataset.GetValues<decimal>(DicomTag.ImagePositionPatient))
                    : dataset.GetSingleValueOrDefault<decimal>(DicomTag.ImagePositionPatient, 0).ToString())
                : string.Empty,
            
            FrameOfReferenceUID = dataset.GetSingleValueOrDefault<string>(DicomTag.FrameOfReferenceUID, string.Empty),
            
            ImageType = dataset.Contains(DicomTag.ImageType)
                ? (dataset.GetValueCount(DicomTag.ImageType) > 1
                    ? string.Join("\\", dataset.GetValues<string>(DicomTag.ImageType))
                    : dataset.GetSingleValueOrDefault<string>(DicomTag.ImageType, string.Empty))
                : string.Empty,
            
            WindowCenter = dataset.Contains(DicomTag.WindowCenter)
                ? (dataset.GetValueCount(DicomTag.WindowCenter) > 1
                    ? string.Join("\\", dataset.GetValues<string>(DicomTag.WindowCenter))
                    : dataset.GetSingleValueOrDefault<string>(DicomTag.WindowCenter, string.Empty))
                : string.Empty,
            
            WindowWidth = dataset.Contains(DicomTag.WindowWidth)
                ? (dataset.GetValueCount(DicomTag.WindowWidth) > 1
                    ? string.Join("\\", dataset.GetValues<string>(DicomTag.WindowWidth))
                    : dataset.GetSingleValueOrDefault<string>(DicomTag.WindowWidth, string.Empty))
                : string.Empty,
            
            CreateTime = now
        });
    }

    private string GetStudyModality(DicomDataset dataset)
    {
        // 首先尝试从ModalitiesInStudy获取
        try
        {
            if (dataset.Contains(DicomTag.ModalitiesInStudy))
            {
                var modalities = dataset.GetValues<string>(DicomTag.ModalitiesInStudy);
                if (modalities != null && modalities.Length > 0)
                {
                    return string.Join("\\", modalities.Where(m => !string.IsNullOrEmpty(m)));
                }
            }
        }
        catch (Exception ex)
        {
            LogWarning("获取ModalitiesInStudy失败: {Error}", ex.Message);
        }

        // 如果没有ModalitiesInStudy，则使用Series级别的Modality
        var modality = dataset.GetSingleValueOrDefault<string>(DicomTag.Modality, string.Empty);
        if (!string.IsNullOrEmpty(modality))
        {
            return modality;
        }

        return string.Empty;
    }

    private void HandleFailedBatch(List<(DicomDataset Dataset, string FilePath)> failedItems)
    {
        // 可以实现失败理逻辑，比如：
        // 1. 写入错误日志文件
        // 2. 存入特定的误表
        // 3. 送告警通知
        // 4. 放入重试队列等
    }

    public async Task SaveDicomDataAsync(DicomDataset dataset, string filePath)
    {
        _dataQueue.Enqueue((dataset, filePath));
        
        // 当队列达到批处理的80%时，主动触发处理
        if (_dataQueue.Count >= _batchSize * 0.8)
        {
            await ProcessQueueAsync();
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;

            try
            {
                // 1. 先停止定时器，防止新的处理被触发
                _processTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                _processTimer?.Dispose();

                // 2. 等待当前处理完成并处理剩余数据
                while (!_dataQueue.IsEmpty)
                {
                    try
                    {
                        // 同步处理剩余数据
                        using var semLock = _processSemaphore.LockOrNull(TimeSpan.FromSeconds(30)); // 给足够的等待时间
                        if (semLock is not null)
                        {
                            ProcessBatchWithRetryAsync().GetAwaiter().GetResult();
                        }
                    }
                    catch (Exception ex)
                    {
                        LogError(ex, "处理剩余数据失败");
                        break;  // 如果处理失败，退出循环
                    }
                }

                // 3. 最后释放信号量
                _processSemaphore?.Dispose();
            }
            catch (Exception ex)
            {
                LogError(ex, "Dispose过程发生错误");
            }
        }
    }

    public async Task<IEnumerable<dynamic>> GetAllStudiesWithPatientInfoAsync()
    {
        using var connection = new SqliteConnection(_connectionString);
        var sql = @"
            SELECT s.StudyInstanceUid, s.PatientId, s.StudyDate, s.StudyTime, 
                   s.StudyDescription, s.AccessionNumber, s.CreateTime,
                   p.PatientName, p.PatientSex, p.PatientBirthDate,
                   (SELECT Modality FROM Series WHERE StudyInstanceUid = s.StudyInstanceUid LIMIT 1) as Modality
            FROM Studies s
            LEFT JOIN Patients p ON s.PatientId = p.PatientId
            ORDER BY s.CreateTime DESC";

        return await connection.QueryAsync(sql);
    }

    public async Task<IEnumerable<Series>> GetSeriesByStudyUidAsync(string studyUid)
    {
        using var connection = new SqliteConnection(_connectionString);
        var sql = @"
            SELECT s.*, 
                   (SELECT COUNT(*) FROM Instances i WHERE i.SeriesInstanceUid = s.SeriesInstanceUid) as NumberOfInstances,
                   (SELECT Modality FROM Studies WHERE StudyInstanceUid = s.StudyInstanceUid) as StudyModality
            FROM Series s
            WHERE s.StudyInstanceUid = @StudyUid
            ORDER BY CAST(s.SeriesNumber as INTEGER)";

        return await connection.QueryAsync<Series>(sql, new { StudyUid = studyUid });
    }

    public async Task<bool> ValidateUserAsync(string username, string password)
    {
        using var connection = new SqliteConnection(_connectionString);
        var hashedPassword = HashPassword(password);
        var count = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM Users WHERE Username = @Username AND Password = @Password",
            new { Username = username, Password = hashedPassword }
        );
        return count > 0;
    }

    public async Task<bool> ChangePasswordAsync(string username, string newPassword)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var hashedPassword = HashPassword(newPassword);
        
        var sql = @"
            UPDATE Users 
            SET Password = @Password 
            WHERE Username = @Username";
        
        var result = await connection.ExecuteAsync(sql, new { 
            Username = username, 
            Password = hashedPassword 
        });

        return result > 0;
    }

    private string HashPassword(string password)
    {
        using var sha256 = SHA256.Create();
        var hashedBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
        return Convert.ToBase64String(hashedBytes);
    }

    public async Task DeleteStudyAsync(string studyInstanceUid)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
  
        try
        {
            // 先检查是否存在相关记录
            var instanceCount = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM Instances WHERE SeriesInstanceUid IN (SELECT SeriesInstanceUid FROM Series WHERE StudyInstanceUid = @StudyInstanceUid)",
                new { StudyInstanceUid = studyInstanceUid },
                transaction: transaction
            );
  
            // 删除与 Series 相关的 Instances
            await connection.ExecuteAsync(
                "DELETE FROM Instances WHERE SeriesInstanceUid IN (SELECT SeriesInstanceUid FROM Series WHERE StudyInstanceUid = @StudyInstanceUid)",
                new { StudyInstanceUid = studyInstanceUid },
                transaction: transaction
            );
  
            // 删除与 Study 相关的 Series
            await connection.ExecuteAsync(
                "DELETE FROM Series WHERE StudyInstanceUid = @StudyInstanceUid",
                new { StudyInstanceUid = studyInstanceUid },
                transaction: transaction
            );
  
            // 删除 Study
            await connection.ExecuteAsync(
                "DELETE FROM Studies WHERE StudyInstanceUid = @StudyInstanceUid",
                new { StudyInstanceUid = studyInstanceUid },
                transaction: transaction
            );
  
            await transaction.CommitAsync();
            LogInformation("成功删除检查 - StudyInstanceUID: {StudyInstanceUid}, 删除实例数: {InstanceCount}", 
                studyInstanceUid, instanceCount);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            LogError(ex, "删除检查失败 - 检查实例UID: {StudyInstanceUid}", studyInstanceUid);
            throw;
        }
    }

    public async Task<Study?> GetStudyAsync(string studyInstanceUid)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        
        var sql = @"
            SELECT s.*, p.PatientName, p.PatientId, p.PatientSex, p.PatientBirthDate
            FROM Studies s
            LEFT JOIN Patients p ON s.PatientId = p.PatientId
            WHERE s.StudyInstanceUid = @StudyInstanceUid";
        
        return await connection.QueryFirstOrDefaultAsync<Study>(
            sql,
            new { StudyInstanceUid = studyInstanceUid }
        );
    }

    public async Task<IEnumerable<Instance>> GetSeriesInstancesAsync(string seriesInstanceUid)
    {
        using var connection = new SqliteConnection(_connectionString);
        var sql = @"
            SELECT * FROM Instances 
            WHERE SeriesInstanceUid = @SeriesInstanceUid 
            ORDER BY CAST(InstanceNumber as INTEGER)";
        
        return await connection.QueryAsync<Instance>(sql, new { SeriesInstanceUid = seriesInstanceUid });
    }

    public async Task<Instance?> GetInstanceAsync(string sopInstanceUid)
    {
        using var connection = new SqliteConnection(_connectionString);
        var sql = "SELECT * FROM Instances WHERE SopInstanceUid = @SopInstanceUid";
        
        return await connection.QueryFirstOrDefaultAsync<Instance>(
            sql, 
            new { SopInstanceUid = sopInstanceUid }
        );
    }

    public List<WorklistItem> GetWorklistItems(
        string patientId,
        string patientName,
        string accessionNumber,
        (string StartDate, string EndDate) dateRange,
        string modality,
        string scheduledStationName)
    {
        try
        {
            using var connection = CreateConnection();
            var sql = @"
                SELECT * FROM Worklist 
                WHERE 1=1
                AND (@PatientId = '' OR PatientId LIKE @PatientId)
                AND (@PatientName = '' OR PatientName LIKE @PatientName)
                AND (@AccessionNumber = '' OR AccessionNumber LIKE @AccessionNumber)
                AND (@StartDate = '' OR @EndDate = '' OR 
                     substr(ScheduledDateTime, 1, 8) >= @StartDate AND 
                     substr(ScheduledDateTime, 1, 8) <= @EndDate)
                AND (@Modality = '' OR Modality = @Modality)
                AND (@ScheduledStationName = '' OR ScheduledStationName = @ScheduledStationName)
                AND Status = 'SCHEDULED'
                ORDER BY CreateTime DESC";

            var parameters = new
            {
                PatientId = string.IsNullOrEmpty(patientId) ? "" : $"%{patientId}%",
                PatientName = string.IsNullOrEmpty(patientName) ? "" : $"%{patientName}%",
                AccessionNumber = string.IsNullOrEmpty(accessionNumber) ? "" : $"%{accessionNumber}%",
                StartDate = dateRange.StartDate,
                EndDate = dateRange.EndDate,
                Modality = string.IsNullOrEmpty(modality) ? "" : modality,
                ScheduledStationName = string.IsNullOrEmpty(scheduledStationName) ? "" : scheduledStationName
            };

            LogDebug("执行工作列表查询 - SQL: {Sql}, 参数: {@Parameters}", sql, parameters);

            var items = connection.Query<WorklistItem>(sql, parameters);
            var result = items?.ToList() ?? new List<WorklistItem>();
            LogInformation("工作列表查询完成 - 返回记录数: {Count}", result.Count);
            return result;
        }
        catch (Exception ex)
        {
            LogError(ex, "工作列表查询失败");
            throw;
        }
    }

    public async Task<PagedResult<StudyInfo>> GetStudiesAsync(
        int page,
        int pageSize,
        string? patientId = null,
        string? patientName = null,
        string? accessionNumber = null,
        string? modality = null,
        DateTime? startDate = null,
        DateTime? endDate = null)
    {
        var sql = new StringBuilder(@"
            SELECT 
                s.StudyInstanceUid,
                s.PatientId,
                p.PatientName,
                p.PatientSex,
                p.PatientBirthDate,
                s.AccessionNumber,
                s.Modality,
                s.StudyDate,
                s.StudyDescription,
                COUNT(DISTINCT i.SopInstanceUid) as NumberOfInstances
            FROM Studies s
            LEFT JOIN Patients p ON s.PatientId = p.PatientId
            LEFT JOIN Series ser ON s.StudyInstanceUid = ser.StudyInstanceUid
            LEFT JOIN Instances i ON ser.SeriesInstanceUid = i.SeriesInstanceUid
            WHERE 1=1");

        var parameters = new DynamicParameters();
        
        if (!string.IsNullOrEmpty(patientId))
        {
            sql.Append(" AND s.PatientId LIKE @PatientId");
            parameters.Add("@PatientId", $"%{patientId}%");
        }

        if (!string.IsNullOrEmpty(patientName))
        {
            sql.Append(" AND p.PatientName LIKE @PatientName");
            parameters.Add("@PatientName", $"%{patientName}%");
        }

        if (!string.IsNullOrEmpty(accessionNumber))
        {
            sql.Append(" AND s.AccessionNumber LIKE @AccessionNumber");
            parameters.Add("@AccessionNumber", $"%{accessionNumber}%");
        }

        if (!string.IsNullOrEmpty(modality))
        {
            sql.Append(" AND s.Modality = @Modality");
            parameters.Add("@Modality", modality);
        }

        if (startDate.HasValue)
        {
            sql.Append(" AND s.StudyDate >= @StartDate");
            parameters.Add("@StartDate", startDate.Value.ToString("yyyyMMdd"));
        }

        if (endDate.HasValue)
        {
            sql.Append(" AND s.StudyDate <= @EndDate");
            parameters.Add("@EndDate", endDate.Value.ToString("yyyyMMdd"));
        }

        // 添加 GROUP BY 子句，包含所有选择的字段
        sql.Append(@" 
            GROUP BY 
                s.StudyInstanceUid,
                s.PatientId,
                p.PatientName,
                p.PatientSex,
                p.PatientBirthDate,
                s.AccessionNumber,
                s.Modality,
                s.StudyDate,
                s.StudyDescription");

        // 获取总记录数（修改子查询以包含 GROUP BY）
        var countSql = $@"
            SELECT COUNT(*) FROM (
                SELECT s.StudyInstanceUid
                FROM Studies s
                LEFT JOIN Patients p ON s.PatientId = p.PatientId
                LEFT JOIN Series ser ON s.StudyInstanceUid = ser.StudyInstanceUid
                LEFT JOIN Instances i ON ser.SeriesInstanceUid = i.SeriesInstanceUid
                WHERE 1=1
                {sql.ToString().Substring(sql.ToString().IndexOf("WHERE 1=1") + 9)}
            ) as t";

        // 添加排序和分页
        sql.Append(" ORDER BY s.StudyDate DESC, s.StudyTime DESC");
        sql.Append(" LIMIT @PageSize OFFSET @Offset");
        
        parameters.Add("@PageSize", pageSize);
        parameters.Add("@Offset", (page - 1) * pageSize);

        await using var connection = new SqliteConnection(_connectionString);
        var totalCount = await connection.ExecuteScalarAsync<int>(countSql, parameters);
        var items = await connection.QueryAsync<StudyInfo>(sql.ToString(), parameters);

        return new PagedResult<StudyInfo>
        {
            Items = items.ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public List<Series> GetSeriesByStudyUid(string studyInstanceUid)
    {
        try
        {
            using var connection = CreateConnection();
            var sql = @"
                SELECT s.*, 
                       (SELECT COUNT(*) FROM Instances i WHERE i.SeriesInstanceUid = s.SeriesInstanceUid) as NumberOfInstances
                FROM Series s
                WHERE s.StudyInstanceUid = @StudyInstanceUid
                ORDER BY CAST(s.SeriesNumber as INTEGER)";

            LogDebug("执行序列查询 - SQL: {Sql}, StudyInstanceUid: {StudyInstanceUid}", 
                sql, studyInstanceUid);

            var series = connection.Query<Series>(sql, new { StudyInstanceUid = studyInstanceUid });

            var result = series?.ToList() ?? new List<Series>();
            LogInformation("序列查询完成 - StudyInstanceUid: {StudyInstanceUid}, 返回记录数: {Count}", 
                studyInstanceUid, result.Count);
            return result;
        }
        catch (Exception ex)
        {
            LogError(ex, "序列查询失败 - StudyInstanceUid: {StudyInstanceUid}", studyInstanceUid);
            return new List<Series>();
        }
    }

    public List<Instance> GetInstancesBySeriesUid(string studyInstanceUid, string seriesInstanceUid)
    {
        try
        {
            using var connection = CreateConnection();
            var sql = @"
                SELECT i.*, s.StudyInstanceUid, s.Modality
                FROM Instances i
                JOIN Series s ON i.SeriesInstanceUid = s.SeriesInstanceUid
                WHERE s.StudyInstanceUid = @StudyInstanceUid 
                AND i.SeriesInstanceUid = @SeriesInstanceUid
                ORDER BY CAST(i.InstanceNumber as INTEGER)";

            LogDebug("执行图像查询 - SQL: {Sql}, StudyInstanceUid: {StudyInstanceUid}, SeriesInstanceUid: {SeriesInstanceUid}", 
                sql, studyInstanceUid, seriesInstanceUid);

            var instances = connection.Query<Instance>(sql, new 
            { 
                StudyInstanceUid = studyInstanceUid,
                SeriesInstanceUid = seriesInstanceUid
            });

            var result = instances?.ToList() ?? new List<Instance>();
            LogInformation("图像查询完成 - StudyInstanceUid: {StudyInstanceUid}, SeriesInstanceUid: {SeriesInstanceUid}, 返回记录数: {Count}", 
                studyInstanceUid, seriesInstanceUid, result.Count);
            return result;
        }
        catch (Exception ex)
        {
            LogError(ex, "图像查询失败 - StudyInstanceUid: {StudyInstanceUid}, SeriesInstanceUid: {SeriesInstanceUid}", 
                studyInstanceUid, seriesInstanceUid);
            return new List<Instance>();
        }
    }

    public IEnumerable<Instance> GetInstancesByStudyUid(string studyInstanceUid)
    {
        try
        {
            using var connection = CreateConnection();
            var sql = @"
                SELECT i.*, s.StudyInstanceUid, s.Modality
                FROM Instances i
                JOIN Series s ON i.SeriesInstanceUid = s.SeriesInstanceUid
                WHERE s.StudyInstanceUid = @StudyInstanceUid
                ORDER BY CAST(i.InstanceNumber as INTEGER)";

            LogDebug("执行实例查询 - SQL: {Sql}, StudyInstanceUid: {StudyInstanceUid}", 
                sql, studyInstanceUid);

            var instances = connection.Query<Instance>(sql, new { StudyInstanceUid = studyInstanceUid });

            var result = instances?.ToList() ?? new List<Instance>();
            LogInformation("实例查询完成 - StudyInstanceUid: {StudyInstanceUid}, 返回记录数: {Count}", 
                studyInstanceUid, result.Count);
            return result;
        }
        catch (Exception ex)
        {
            LogError(ex, "实例查询失败 - StudyInstanceUid: {StudyInstanceUid}", studyInstanceUid);
            return new List<Instance>();
        }
    }

    public void UpdateWorklistStatus(string scheduledStepId, string status)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE Worklist 
            SET Status = @Status 
            WHERE ScheduledProcedureStepID = @StepID";

        command.Parameters.AddWithValue("@Status", status);
        command.Parameters.AddWithValue("@StepID", scheduledStepId);

        command.ExecuteNonQuery();
    }

    #region PrintSCP Operations

    /// <summary>
    /// 添加新的打印任务
    /// </summary>
    public async Task<bool> AddPrintJobAsync(PrintJob job)
    {
        try
        {
            using var connection = CreateConnection();
            var parameters = new DynamicParameters();
            
            // 基本信息
            parameters.Add("@JobId", job.JobId);
            parameters.Add("@FilmSessionId", job.FilmSessionId);
            parameters.Add("@FilmBoxId", job.FilmBoxId);
            parameters.Add("@CallingAE", job.CallingAE);
            parameters.Add("@Status", job.Status.ToString());
            parameters.Add("@ErrorMessage", job.ErrorMessage);

            // Film Session 参数
            parameters.Add("@NumberOfCopies", job.NumberOfCopies);
            parameters.Add("@PrintPriority", job.PrintPriority);
            parameters.Add("@MediumType", job.MediumType);
            parameters.Add("@FilmDestination", job.FilmDestination);

            // Film Box 参数
            parameters.Add("@PrintInColor", job.PrintInColor ? 1 : 0);  // 布尔值转换为整数
            parameters.Add("@FilmOrientation", job.FilmOrientation);
            parameters.Add("@FilmSizeID", job.FilmSizeID);
            parameters.Add("@ImageDisplayFormat", job.ImageDisplayFormat);
            parameters.Add("@MagnificationType", job.MagnificationType);
            parameters.Add("@SmoothingType", job.SmoothingType);
            parameters.Add("@BorderDensity", job.BorderDensity);
            parameters.Add("@EmptyImageDensity", job.EmptyImageDensity);
            parameters.Add("@Trim", job.Trim);

            // 其他参数
            parameters.Add("@ImagePath", job.ImagePath);
            parameters.Add("@StudyInstanceUID", job.StudyInstanceUID);
            parameters.Add("@CreateTime", job.CreateTime);
            parameters.Add("@UpdateTime", job.UpdateTime);

            var sql = @"
                INSERT INTO PrintJobs (
                    JobId, FilmSessionId, FilmBoxId, CallingAE, Status, ErrorMessage,
                    NumberOfCopies, PrintPriority, MediumType, FilmDestination,
                    PrintInColor, FilmOrientation, FilmSizeID, ImageDisplayFormat,
                    MagnificationType, SmoothingType, BorderDensity, EmptyImageDensity,
                    Trim, ImagePath, StudyInstanceUID,
                    CreateTime, UpdateTime
                ) VALUES (
                    @JobId, @FilmSessionId, @FilmBoxId, @CallingAE, @Status, @ErrorMessage,
                    @NumberOfCopies, @PrintPriority, @MediumType, @FilmDestination,
                    @PrintInColor, @FilmOrientation, @FilmSizeID, @ImageDisplayFormat,
                    @MagnificationType, @SmoothingType, @BorderDensity, @EmptyImageDensity,
                    @Trim, @ImagePath, @StudyInstanceUID,
                    @CreateTime, @UpdateTime
                )";

            var result = await connection.ExecuteAsync(sql, parameters);
            LogDebug("添加打印任务 - JobId: {JobId}, 结果: {Result}", job.JobId, result > 0);
            return result > 0;
        }
        catch (Exception ex)
        {
            LogError(ex, "添加打印任务失败 - JobId: {JobId}", job.JobId);
            throw;
        }
    }

    /// <summary>
    /// 更打印任务状态和图像路径
    /// </summary>
    public async Task<bool> UpdatePrintJobStatusAsync(string jobId, PrintJobStatus status, string? imagePath = null)
    {
        try
        {
            using var connection = CreateConnection();
            var updates = new List<string> { "Status = @Status", "UpdateTime = @UpdateTime" };
            var parameters = new DynamicParameters();
            parameters.Add("@JobId", jobId);
            parameters.Add("@Status", status.ToString());
            parameters.Add("@UpdateTime", DateTime.Now);

            if (imagePath != null)
            {
                updates.Add("ImagePath = @ImagePath");
                parameters.Add("@ImagePath", imagePath);
            }

            var sql = $"UPDATE PrintJobs SET {string.Join(", ", updates)} WHERE JobId = @JobId";
            var result = await connection.ExecuteAsync(sql, parameters);
            LogDebug("更新打印任务状态 - JobId: {JobId}, 状态: {Status}, 结果: {Result}", jobId, status, result > 0);
            return result > 0;
        }
        catch (Exception ex)
        {
            LogError(ex, "更新打印任务状态失败 - JobId: {JobId}, 状态: {Status}", jobId, status);
            throw;
        }
    }

    /// <summary>
    /// 更打印任务信息
    /// </summary>
    public async Task<bool> UpdatePrintJobAsync(string filmSessionId, string? filmBoxId = null, Dictionary<string, object>? parameters = null)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync();
        using var transaction = await connection.BeginTransactionAsync();
        
        try
        {
            var updates = new List<string>();
            var dbParams = new DynamicParameters();
            dbParams.Add("@FilmSessionId", filmSessionId);
            dbParams.Add("@UpdateTime", DateTime.Now);

            if (filmBoxId != null)
            {
                updates.Add("FilmBoxId = @FilmBoxId");
                dbParams.Add("@FilmBoxId", filmBoxId);
            }

            if (parameters != null)
            {
                foreach (var param in parameters)
                {
                    updates.Add($"{param.Key} = @{param.Key}");
                    dbParams.Add($"@{param.Key}", param.Value);
                    LogDebug("更新参数 - {ParamName}: {ParamValue}", param.Key, param.Value);
                }
            }

            updates.Add("UpdateTime = @UpdateTime");

            var sql = $"UPDATE PrintJobs SET {string.Join(", ", updates)} WHERE FilmSessionId = @FilmSessionId";
            var result = await connection.ExecuteAsync(sql, dbParams, transaction);
            await transaction.CommitAsync();
            return result > 0;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            LogError(ex, "更新打印任务失败 - FilmSessionId: {FilmSessionId}", filmSessionId);
            throw;
        }
    }

    /// <summary>
    /// 根据FilmBoxId获取打印任务
    /// </summary>
    public async Task<PrintJob?> GetPrintJobByFilmBoxIdAsync(string filmBoxId)
    {
        try
        {
            using var connection = CreateConnection();
            var sql = @"
                SELECT 
                    JobId, FilmSessionId, FilmBoxId, CallingAE, Status, ErrorMessage,
                    NumberOfCopies, PrintPriority, MediumType, FilmDestination,
                    CASE WHEN PrintInColor = 1 THEN true ELSE false END as PrintInColor,
                    FilmOrientation, FilmSizeID, ImageDisplayFormat,
                    MagnificationType, SmoothingType, BorderDensity, EmptyImageDensity,
                    Trim, ImagePath, StudyInstanceUID,
                    CreateTime, UpdateTime
                FROM PrintJobs 
                WHERE FilmBoxId = @FilmBoxId";

            return await connection.QueryFirstOrDefaultAsync<PrintJob>(sql, new { FilmBoxId = filmBoxId });
        }
        catch (Exception ex)
        {
            LogError(ex, "获取打印任务失败 - FilmBoxId: {FilmBoxId}", filmBoxId);
            throw;
        }
    }

    /// <summary>
    /// 获取指定状态的打印任务列表
    /// </summary>
    public async Task<IEnumerable<PrintJob>> GetPrintJobsByStatusAsync(string status)
    {
        try
        {
            using var connection = CreateConnection();
            var sql = "SELECT * FROM PrintJobs WHERE Status = @Status ORDER BY CreateTime DESC";
            return await connection.QueryAsync<PrintJob>(sql, new { Status = status });
        }
        catch (Exception ex)
        {
            LogError(ex, "获取打印任务列表失败 - Status: {Status}", status);
            throw;
        }
    }

    /// <summary>
    /// 获取最近的打印任务
    /// </summary>
    public async Task<PrintJob?> GetMostRecentPrintJobAsync()
    {
        try
        {
            using var connection = CreateConnection();
            var sql = "SELECT * FROM PrintJobs WHERE FilmBoxId IS NOT NULL ORDER BY CreateTime DESC LIMIT 1";
            return await connection.QueryFirstOrDefaultAsync<PrintJob>(sql);
        }
        catch (Exception ex)
        {
            LogError(ex, "获取最近打印任务失败");
            throw;
        }
    }

    #endregion

    #region Print Management API

    /// <summary>
    /// 获取所有打印任务列表
    /// </summary>
    public async Task<(List<PrintJob> Items, int Total, int Page, int PageSize, int TotalPages)> GetPrintJobsAsync(
        string? callingAE = null,
        string? studyUID = null,
        string? status = null,
        DateTime? date = null,
        int page = 1,
        int pageSize = 10)
    {
        try
        {
            using var connection = CreateConnection();
            
            // 构建查询
            var sql = new StringBuilder("SELECT COUNT(*) FROM PrintJobs WHERE 1=1");
            var parameters = new DynamicParameters();

            if (!string.IsNullOrEmpty(callingAE))
            {
                sql.Append(" AND CallingAE LIKE @CallingAE");
                parameters.Add("@CallingAE", $"%{callingAE}%");
            }

            if (!string.IsNullOrEmpty(studyUID))
            {
                sql.Append(" AND StudyInstanceUID LIKE @StudyUID");
                parameters.Add("@StudyUID", $"%{studyUID}%");
            }

            if (!string.IsNullOrEmpty(status))
            {
                sql.Append(" AND Status = @Status");
                parameters.Add("@Status", status);
            }

            if (date.HasValue)
            {
                sql.Append(" AND DATE(CreateTime) = DATE(@Date)");
                parameters.Add("@Date", date.Value.Date);
            }

            // 获取总记录数
            var total = await connection.ExecuteScalarAsync<int>(sql.ToString(), parameters);
            
            // 计算总页数
            var totalPages = (int)Math.Ceiling(total / (double)pageSize);
            
            // 构建分页查询
            sql = new StringBuilder(@"
                SELECT * FROM PrintJobs WHERE 1=1");

            if (!string.IsNullOrEmpty(callingAE))
            {
                sql.Append(" AND CallingAE LIKE @CallingAE");
            }

            if (!string.IsNullOrEmpty(studyUID))
            {
                sql.Append(" AND StudyInstanceUID LIKE @StudyUID");
            }

            if (!string.IsNullOrEmpty(status))
            {
                sql.Append(" AND Status = @Status");
            }

            if (date.HasValue)
            {
                sql.Append(" AND DATE(CreateTime) = DATE(@Date)");
            }

            sql.Append(" ORDER BY CreateTime DESC LIMIT @PageSize OFFSET @Offset");
            parameters.Add("@PageSize", pageSize);
            parameters.Add("@Offset", (page - 1) * pageSize);

            var items = await connection.QueryAsync<PrintJob>(sql.ToString(), parameters);

            return (items.ToList(), total, page, pageSize, totalPages);
        }
        catch (Exception ex)
        {
            LogError(ex, "获取打印任务列表失败");
            throw;
        }
    }

    /// <summary>
    /// 获取单个打印任务
    /// </summary>
    public async Task<PrintJob?> GetPrintJobAsync(string jobId)
    {
        using var connection = CreateConnection();
        var sql = "SELECT * FROM PrintJobs WHERE JobId = @JobId";
        return await connection.QueryFirstOrDefaultAsync<PrintJob>(sql, new { JobId = jobId });
    }

    /// <summary>
    /// 删除打印任务
    /// </summary>
    public async Task<bool> DeletePrintJobAsync(string jobId)
    {
        try
        {
            using var connection = CreateConnection();
            var sql = "DELETE FROM PrintJobs WHERE JobId = @JobId";
            var rowsAffected = await connection.ExecuteAsync(sql, new { JobId = jobId });
            LogDebug("删除印任务 - JobId: {JobId}, 成功: {Success}", jobId, rowsAffected > 0);
            return rowsAffected > 0;
        }
        catch (Exception ex)
        {
            LogError(ex, "删除打印任务失败 - JobId: {JobId}", jobId);
            throw;
        }
    }

    #endregion

    public IEnumerable<Patient> GetPatients(string patientId, string patientName)
    {
        try
        {
            using var connection = CreateConnection();
            var sql = @"
                SELECT 
                    p.PatientId, 
                    p.PatientName, 
                    p.PatientBirthDate, 
                    p.PatientSex,
                    p.CreateTime,
                    COUNT(DISTINCT s.StudyInstanceUid) as NumberOfStudies,
                    COUNT(DISTINCT ser.SeriesInstanceUid) as NumberOfSeries,
                    COUNT(DISTINCT i.SopInstanceUid) as NumberOfInstances
                FROM Patients p
                LEFT JOIN Studies s ON p.PatientId = s.PatientId
                LEFT JOIN Series ser ON s.StudyInstanceUid = ser.StudyInstanceUid
                LEFT JOIN Instances i ON ser.SeriesInstanceUid = i.SeriesInstanceUid
                WHERE 1=1";

            var parameters = new DynamicParameters();

            if (!string.IsNullOrEmpty(patientId))
            {
                sql += " AND p.PatientId LIKE @PatientId";
                parameters.Add("@PatientId", $"%{patientId}%");
            }

            if (!string.IsNullOrEmpty(patientName))
            {
                sql += " AND p.PatientName LIKE @PatientName";
                parameters.Add("@PatientName", $"%{patientName}%");
            }

            sql += @" 
                GROUP BY p.PatientId, p.PatientName, p.PatientBirthDate, p.PatientSex, p.CreateTime
                ORDER BY p.PatientName";

            LogDebug("执行Patient查询 - SQL: {Sql}, PatientId: {PatientId}, PatientName: {PatientName}", 
                sql, patientId, patientName);

            var patients = connection.Query<Patient>(sql, parameters).ToList();

            LogInformation("Patient查询完成 - 返回记录数: {Count}", patients.Count);

            return patients;
        }
        catch (Exception ex)
        {
            LogError(ex, "Patient查询失败 - PatientId: {PatientId}, PatientName: {PatientName}", 
                patientId, patientName);
            return Enumerable.Empty<Patient>();
        }
    }

    public async Task UpdateStudyModalityAsync(string studyInstanceUid, string modality)
    {
        try
        {
            using var connection = CreateConnection();
            var sql = @"
                UPDATE Studies 
                SET Modality = @Modality 
                WHERE StudyInstanceUid = @StudyInstanceUid 
                AND (Modality IS NULL OR Modality = '')";

            await connection.ExecuteAsync(sql, new { 
                StudyInstanceUid = studyInstanceUid, 
                Modality = modality 
            });

            LogDebug("更新Study Modality - StudyInstanceUID: {StudyInstanceUid}, Modality: {Modality}", 
                studyInstanceUid, modality);
        }
        catch (Exception ex)
        {
            LogError(ex, "更新Study Modality失败 - StudyInstanceUID: {StudyInstanceUid}, Modality: {Modality}", 
                studyInstanceUid, modality);
        }
    }

    // 保原有法，供 QRSCP 使用
    public List<Study> GetStudies(
        string patientId, 
        string patientName, 
        string accessionNumber, 
        (string StartDate, string EndDate) dateRange,
        string[]? modalities,
        string? studyInstanceUid = null,
        int? offset = null,
        int? limit = null)
    {
        try
        {
            using var connection = CreateConnection();
            // 先查询总数
            var countSql = @"
                SELECT COUNT(DISTINCT s.StudyInstanceUid)
                FROM Studies s
                LEFT JOIN Patients p ON s.PatientId = p.PatientId
                WHERE 1=1
                AND (@PatientId = '' OR s.PatientId LIKE @PatientId)
                AND (@PatientName = '' OR p.PatientName LIKE @PatientName)
                AND (@AccessionNumber = '' OR s.AccessionNumber LIKE @AccessionNumber)
                AND (@StartDate = '' OR s.StudyDate >= @StartDate)
                AND (@EndDate = '' OR s.StudyDate <= @EndDate)
                AND (@ModCount = 0 OR s.Modality IN @Modalities)
                AND (@StudyInstanceUid = '' OR s.StudyInstanceUid = @StudyInstanceUid)";

            var sql = @"
                SELECT 
                    s.*,
                    p.PatientName,
                    p.PatientSex,
                    p.PatientBirthDate,
                    COUNT(DISTINCT ser.SeriesInstanceUid) as NumberOfStudyRelatedSeries,
                    COUNT(DISTINCT i.SopInstanceUid) as NumberOfStudyRelatedInstances
                FROM Studies s
                LEFT JOIN Patients p ON s.PatientId = p.PatientId
                LEFT JOIN Series ser ON s.StudyInstanceUid = ser.StudyInstanceUid
                LEFT JOIN Instances i ON ser.SeriesInstanceUid = i.SeriesInstanceUid
                WHERE 1=1
                AND (@PatientId = '' OR s.PatientId LIKE @PatientId)
                AND (@PatientName = '' OR p.PatientName LIKE @PatientName)
                AND (@AccessionNumber = '' OR s.AccessionNumber LIKE @AccessionNumber)
                AND (@StartDate = '' OR s.StudyDate >= @StartDate)
                AND (@EndDate = '' OR s.StudyDate <= @EndDate)
                AND (@ModCount = 0 OR s.Modality IN @Modalities)
                AND (@StudyInstanceUid = '' OR s.StudyInstanceUid = @StudyInstanceUid)
                GROUP BY 
                    s.StudyInstanceUid,
                    s.PatientId,
                    s.StudyDate,
                    s.StudyTime,
                    s.StudyDescription,
                    s.AccessionNumber,
                    s.Modality,
                    s.CreateTime,
                    p.PatientName,
                    p.PatientSex,
                    p.PatientBirthDate
                ORDER BY s.CreateTime DESC";

            // 如果指定了分页参数，添加分页
            if (offset.HasValue && limit.HasValue)
            {
                sql += " LIMIT @Limit OFFSET @Offset";
            }

            var parameters = new
            {
                PatientId = string.IsNullOrEmpty(patientId) ? "" : $"%{patientId}%",
                PatientName = string.IsNullOrEmpty(patientName) ? "" : $"%{patientName}%",
                AccessionNumber = string.IsNullOrEmpty(accessionNumber) ? "" : $"%{accessionNumber}%",
                StartDate = dateRange.StartDate,
                EndDate = dateRange.EndDate,
                ModCount = modalities?.Length ?? 0,
                Modalities = (modalities?.Length ?? 0) > 0 ? modalities : new[] { "" },
                StudyInstanceUid = studyInstanceUid ?? "",
                Offset = offset,
                Limit = limit
            };

            LogDebug("执行检查查询 - SQL: {Sql}, 参数: {@Parameters}", sql, parameters);

            // 获取总数
            var totalCount = connection.ExecuteScalar<int>(countSql, parameters);

            var studies = connection.Query<Study>(sql, parameters);
            var result = studies?.ToList() ?? new List<Study>();
            
            LogInformation("检查查询完成 - 返回记录数: {Count}/{Total}, 日期范围: {StartDate} - {EndDate}, StudyInstanceUID: {StudyUID}", 
                result.Count, totalCount, dateRange.StartDate ?? "", dateRange.EndDate ?? "", studyInstanceUid ?? "");

            return result;
        }
        catch (Exception ex)
        {
            LogError(ex, "检查查询失败");
            return new List<Study>();
        }
    }

    public async Task<PrintJob?> GetPrintJobByIdAsync(string jobId)
    {
        using var connection = new SqliteConnection(_connectionString);
        var sql = "SELECT * FROM PrintJobs WHERE JobId = @JobId";
        return await connection.QueryFirstOrDefaultAsync<PrintJob>(sql, new { JobId = jobId });
    }

    public Configuration.PrinterConfig? GetPrinterByName(string printerName)
    {
        var printers = _settings.PrintSCU?.Printers;
        return printers?.FirstOrDefault(p => p.Name == printerName);
    }

    public List<Configuration.PrinterConfig> GetPrinters()
    {
        return _settings.PrintSCU?.Printers ?? new List<Configuration.PrinterConfig>();
    }

    public async Task<IEnumerable<Series>> GetSeriesAsync(string studyInstanceUid)
    {
        using var connection = new SqliteConnection(_connectionString);
        var sql = @"
            SELECT s.*, COUNT(i.SopInstanceUid) as NumberOfInstances
            FROM Series s
            LEFT JOIN Instances i ON s.SeriesInstanceUid = i.SeriesInstanceUid
            WHERE s.StudyInstanceUid = @StudyInstanceUid
            GROUP BY s.SeriesInstanceUid, s.StudyInstanceUid, s.Modality, 
                     s.SeriesNumber, s.SeriesDescription, s.SliceThickness, 
                     s.SeriesDate, s.CreateTime";

        return await connection.QueryAsync<Series>(sql, new { StudyInstanceUid = studyInstanceUid });
    }
}
