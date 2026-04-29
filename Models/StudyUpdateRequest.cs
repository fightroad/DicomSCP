namespace DicomSCP.Models;

public class StudyUpdateRequest
{
    public string? PatientName { get; set; }
    public string? PatientSex { get; set; }
    public string? PatientBirthDate { get; set; }
    public string? StudyDate { get; set; }
    public string? StudyDescription { get; set; }
    public string? AccessionNumber { get; set; }
    public string? InstitutionName { get; set; }
    public string? Remark { get; set; }
}

