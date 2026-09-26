using System;
using System.ComponentModel.DataAnnotations;

namespace DicomSCP.Models;

public class PrintJob
{
    // 基本信息
    public string JobId { get; set; } = "";
    public string FilmSessionId { get; set; } = "";
    public string FilmBoxId { get; set; } = "";
    public string CallingAE { get; set; } = "";
    public PrintJobStatus Status { get; set; }
    public string ErrorMessage { get; set; } = "";

    // Film Session 参数
    public int NumberOfCopies { get; set; } = 1;
    public string PrintPriority { get; set; } = "LOW";
    public string MediumType { get; set; } = "BLUE FILM";
    public string FilmDestination { get; set; } = "MAGAZINE";

    // Film Box 参数
    public bool PrintInColor { get; set; } = false;
    public string FilmOrientation { get; set; } = "PORTRAIT";
    public string FilmSizeID { get; set; } = "8INX10IN";
    public string ImageDisplayFormat { get; set; } = "STANDARD\\1,1";
    public string MagnificationType { get; set; } = "REPLICATE";
    public string SmoothingType { get; set; } = "MEDIUM";
    public string BorderDensity { get; set; } = "BLACK";
    public string EmptyImageDensity { get; set; } = "BLACK";
    public string Trim { get; set; } = "NO";

    // 图像信息
    public string ImagePath { get; set; } = "";

    // 研究信息
    public string StudyInstanceUID { get; set; } = "";

    // 时间戳
    public DateTime CreateTime { get; set; }
    public DateTime UpdateTime { get; set; }
}

public enum PrintJobStatus
{
    Created,
    ImageReceived,
    Completed,
    Failed
}

// 打印请求模型
public class PrintRequest
{
    [Required(ErrorMessage = "文件路径不能为空")]
    public string FilePath { get; set; } = string.Empty;

    [Required(ErrorMessage = "打印机AE Title不能为空")]
    public string CalledAE { get; set; } = string.Empty;

    [Required(ErrorMessage = "主机名不能为空")]
    public string HostName { get; set; } = string.Empty;

    [Range(1, 65535, ErrorMessage = "端口号必须在1-65535之间")]
    public int Port { get; set; }

    [Range(1, 99, ErrorMessage = "打印份数必须在1-99之间")]
    public int NumberOfCopies { get; set; } = 1;

    public bool EnableDpi { get; set; } = false;
    public int? Dpi { get; set; }  // 只在EnableDpi=true时使用
    public string PrintPriority { get; set; } = string.Empty;
    public string MediumType { get; set; } = string.Empty;
    public string FilmDestination { get; set; } = string.Empty;
    public string FilmOrientation { get; set; } = string.Empty;
    public string FilmSizeID { get; set; } = string.Empty;
    public string ImageDisplayFormat { get; set; } = string.Empty;
    public string MagnificationType { get; set; } = string.Empty;
    public string SmoothingType { get; set; } = string.Empty;
    public string BorderDensity { get; set; } = string.Empty;
    public string EmptyImageDensity { get; set; } = string.Empty;
    public string Trim { get; set; } = string.Empty;
    public bool PrintInColor { get; set; }
} 