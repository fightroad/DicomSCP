using System.ComponentModel.DataAnnotations;

namespace DicomSCP.Configuration;

public class PrinterConfig
{
    public string Name { get; set; } = "";
    public string AeTitle { get; set; } = "";
    public string HostName { get; set; } = "";
    public int Port { get; set; } = 104;
    public bool IsDefault { get; set; }
    public string Description { get; set; } = "";
}

public class PrintScuConfig
{
    public string AeTitle { get; set; } = "";
    public List<PrinterConfig> Printers { get; set; } = new();
}

public class DicomSettings
{
    [Required]
    [RegularExpression(@"^[A-Za-z0-9\-_]{1,16}$")]
    public string AeTitle { get; set; } = "STORESCP";

    [Range(1, 65535)]
    public int StoreSCPPort { get; set; } = 11112;

    [Required]
    public string StoragePath { get; set; } = "./received_files";

    [Required]
    public string TempPath { get; set; } = "./temp_files";

    public AdvancedSettings Advanced { get; set; } = new();

    public WorklistSCPSettings WorklistSCP { get; set; } = new();

    public QRSCPSettings QRSCP { get; set; } = new();

    public PrintSCPSettings PrintSCP { get; set; } = new();

    public PrintScuConfig? PrintSCU { get; set; }
    
    public List<PrinterConfig> Printers { get; set; } = new();
}

public class WorklistSCPSettings
{
    [Required]
    [RegularExpression(@"^[A-Za-z0-9\-_]{1,16}$")]
    public string AeTitle { get; set; } = "WORKLISTSCP";

    [Range(1, 65535)]
    public int Port { get; set; } = 11113;

    public bool ValidateCallingAE { get; set; } = false;
    public string[] AllowedCallingAEs { get; set; } = Array.Empty<string>();
}

public class AdvancedSettings
{
    public bool ValidateCallingAE { get; set; } = false;
    public string[] AllowedCallingAEs { get; set; } = Array.Empty<string>();

    public bool EnableCompression { get; set; } = false;
    public string PreferredTransferSyntax { get; set; } = "JPEG2000Lossless";
}

public class QRSCPSettings
{
    public string AeTitle { get; set; } = "QRSCP";
    public int Port { get; set; } = 11114;
    public bool ValidateCallingAE { get; set; }
    public List<string> AllowedCallingAEs { get; set; } = new();
    public List<MoveDestination> MoveDestinations { get; set; } = new();
}

public class MoveDestination
{
    public string Name { get; set; } = string.Empty;
    public string AeTitle { get; set; } = string.Empty;
    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 11112;
}

public class PrintSCPSettings
{
    [Required]
    [RegularExpression(@"^[A-Za-z0-9\-_]{1,16}$")]
    public string AeTitle { get; set; } = "PRINTSCP";

    [Range(1, 65535)]
    public int Port { get; set; } = 11115;

    public bool ValidateCallingAE { get; set; } = false;
    public string[] AllowedCallingAEs { get; set; } = Array.Empty<string>();
} 