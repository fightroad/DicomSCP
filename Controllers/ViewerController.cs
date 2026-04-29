using DicomSCP.Repository;
using DicomSCP.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace DicomSCP.Controllers;

[Route("viewer")]
[AllowAnonymous]
public class ViewerController(DicomRepository repository) : ControllerBase
{
    private readonly DicomRepository _repository = repository;

    [HttpGet("ohif/{studyInstanceUid}")]
    public async Task<IActionResult> GetStudyMetadata(string studyInstanceUid)
    {
        try
        {
            // 获取研究信息
            var study = await _repository.GetStudyAsync(studyInstanceUid);
            if (study == null)
            {
                return NotFound("Study not found");
            }

            // 获取系列信息
            var seriesList = await _repository.GetSeriesAsync(studyInstanceUid);
            
            // 获取所有实例并计算总数
            var totalInstances = 0;
            foreach (var series in seriesList)
            {
                var instances = await _repository.GetSeriesInstancesAsync(series.SeriesInstanceUid);
                totalInstances += instances.Count();
            }
            
            // 构建响应模型
            var response = new ViewerStudyResponse
            {
                Studies = new List<ViewerStudy>
                {
                    new ViewerStudy
                    {
                        StudyInstanceUID = study.StudyInstanceUid,
                        NumInstances = totalInstances,  // 使用计算出的实例总数
                        Modalities = study.Modality,
                        StudyDate = study.StudyDate,
                        StudyTime = study.StudyTime,
                        PatientName = study.PatientName,
                        PatientID = study.PatientId,
                        AccessionNumber = study.AccessionNumber,
                        PatientSex = study.PatientSex,
                        Series = await GetSeriesMetadata(seriesList)
                    }
                }
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error retrieving study metadata: {ex.Message}");
        }
    }

    [HttpGet("weasis/{studyInstanceUid}")]
    public async Task<IActionResult> GetWeasisManifest(string studyInstanceUid)
    {
        try
        {
            // 获取研究信息
            var study = await _repository.GetStudyAsync(studyInstanceUid);
            if (study == null)
            {
                return NotFound("Study not found");
            }

            // 获取系列信息
            var seriesList = await _repository.GetSeriesAsync(studyInstanceUid);

            // 构建XML
            var baseUrl = $"{Request.Scheme}://{Request.Host}";

            // 定义命名空间
            XNamespace ns = "http://www.weasis.org/xsd/2.5";
            XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";

            var manifest = new XElement(ns + "manifest");
            manifest.Add(new XAttribute(XNamespace.Xmlns + "xsi", xsi));

            var arcQuery = new XElement(ns + "arcQuery",
                new XAttribute("additionnalParameters", ""),
                new XAttribute("arcId", "1001"),
                new XAttribute("baseUrl", $"{baseUrl}/wado"),
                new XAttribute("requireOnlySOPInstanceUID", "false")
            );

            var patient = new XElement(ns + "Patient",
                new XAttribute("PatientID", study.PatientId),
                new XAttribute("PatientName", study.PatientName ?? ""),
                new XAttribute("PatientSex", study.PatientSex ?? "")
            );

            var studyElement = new XElement(ns + "Study",
                new XAttribute("AccessionNumber", study.AccessionNumber ?? ""),
                new XAttribute("StudyDate", study.StudyDate ?? ""),
                new XAttribute("StudyDescription", study.StudyDescription ?? ""),
                new XAttribute("StudyInstanceUID", study.StudyInstanceUid),
                new XAttribute("StudyTime", study.StudyTime ?? "")
            );

            foreach (var series in seriesList)
            {
                var instances = await _repository.GetSeriesInstancesAsync(series.SeriesInstanceUid);
                var seriesElement = new XElement(ns + "Series",
                    new XAttribute("Modality", series.Modality ?? ""),
                    new XAttribute("SeriesDescription", series.SeriesDescription ?? ""),
                    new XAttribute("SeriesInstanceUID", series.SeriesInstanceUid),
                    new XAttribute("SeriesNumber", series.SeriesNumber ?? "")
                );

                foreach (var instance in instances)
                {
                    seriesElement.Add(new XElement(ns + "Instance",
                        new XAttribute("InstanceNumber", instance.InstanceNumber ?? ""),
                        new XAttribute("SOPInstanceUID", instance.SopInstanceUid)
                    ));
                }

                studyElement.Add(seriesElement);
            }

            patient.Add(studyElement);
            arcQuery.Add(patient);
            manifest.Add(arcQuery);

            var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null), manifest);

            return Content(doc.ToString(), "application/xml");
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error generating Weasis manifest: {ex.Message}");
        }
    }

    private async Task<List<ViewerSeries>> GetSeriesMetadata(IEnumerable<Series> seriesList)
    {
        var result = new List<ViewerSeries>();
        
        foreach (var series in seriesList)
        {
            // 使用现有的GetSeriesInstancesAsync方法
            var instances = await _repository.GetSeriesInstancesAsync(series.SeriesInstanceUid);
            
            result.Add(new ViewerSeries
            {
                SeriesInstanceUID = series.SeriesInstanceUid,
                SeriesNumber = series.SeriesNumber,
                Modality = series.Modality,
                SliceThickness = decimal.TryParse(series.SliceThickness, out var thickness) ? thickness : 0,
                Instances = instances.Select(i => new ViewerInstance
                {
                    Metadata = new InstanceMetadata
                    {
                        Columns = i.Columns,
                        Rows = i.Rows,
                        InstanceNumber = i.InstanceNumber,
                        SOPClassUID = i.SopClassUid,
                        PhotometricInterpretation = i.PhotometricInterpretation,
                        BitsAllocated = i.BitsAllocated,
                        BitsStored = i.BitsStored,
                        PixelRepresentation = i.PixelRepresentation,
                        SamplesPerPixel = i.SamplesPerPixel,
                        PixelSpacing = ParsePixelSpacing(i.PixelSpacing),
                        HighBit = i.HighBit,
                        ImageOrientationPatient = ParseImageOrientationPatient(i.ImageOrientationPatient),
                        ImagePositionPatient = ParseImagePositionPatient(i.ImagePositionPatient),
                        FrameOfReferenceUID = i.FrameOfReferenceUID,
                        ImageType = ParseMultiValue(i.ImageType),
                        Modality = series.Modality,
                        SOPInstanceUID = i.SopInstanceUid,
                        SeriesInstanceUID = i.SeriesInstanceUid,
                        StudyInstanceUID = series.StudyInstanceUid,
                        WindowCenter = ParseWindowValue(i.WindowCenter),
                        WindowWidth = ParseWindowValue(i.WindowWidth),
                        SeriesDate = series.SeriesDate
                    },
                    Url = GenerateWadoUrl(series.StudyInstanceUid, i.SeriesInstanceUid, i.SopInstanceUid)
                }).ToList()
            });
        }

        return result;
    }

    private decimal[] ParsePixelSpacing(string? value)
    {
        if (string.IsNullOrEmpty(value)) return Array.Empty<decimal>();
        return value.Split('\\')
            .Select(v => decimal.TryParse(v, out var result) ? result : 0)
            .ToArray();
    }

    private decimal[] ParseVectorValues(string? value)
    {
        if (string.IsNullOrEmpty(value)) return Array.Empty<decimal>();
        return value.Split('\\')
            .Select(v => decimal.TryParse(v, out var result) ? result : 0)
            .ToArray();
    }

    private string[] ParseMultiValue(string? value)
    {
        if (string.IsNullOrEmpty(value)) return Array.Empty<string>();
        return value.Split('\\');
    }

    private decimal ParseWindowValue(string? value)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        var values = value.Split('\\');
        return decimal.TryParse(values[0], out var result) ? result : 0;
    }

    private string GenerateWadoUrl(string studyUid, string seriesUid, string instanceUid)
    {
        // 直接使用请求中的 Host（包含域名和端口）
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
            
        return $"dicomweb:{baseUrl}/wado?requestType=WADO" +
               $"&studyUID={studyUid}" +
               $"&seriesUID={seriesUid}" +
               $"&objectUID={instanceUid}" +
               $"&contentType=application/dicom";
    }

    private decimal[] ParseImageOrientationPatient(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return new decimal[] { 1, 0, 0, 0, 1, 0 };
        }
        return value.Split('\\').Select(v => decimal.TryParse(v, out var result) ? result : 0).ToArray();
    }

    private decimal[] ParseImagePositionPatient(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return new decimal[] { 0, 0, 0 };
        }
        return value.Split('\\').Select(v => decimal.TryParse(v, out var result) ? result : 0).ToArray();
    }
}

// 响应模型
public class ViewerStudyResponse
{
    [JsonPropertyName("studies")]
    public List<ViewerStudy> Studies { get; set; } = new();
}

public class ViewerStudy
{
    [JsonPropertyName("StudyInstanceUID")]
    public string StudyInstanceUID { get; set; } = string.Empty;
    
    [JsonPropertyName("NumInstances")]
    public int NumInstances { get; set; }
    
    [JsonPropertyName("Modalities")]
    public string? Modalities { get; set; }
    
    [JsonPropertyName("StudyDate")]
    public string? StudyDate { get; set; }
    
    [JsonPropertyName("StudyTime")]
    public string? StudyTime { get; set; }
    
    [JsonPropertyName("PatientName")]
    public string? PatientName { get; set; }
    
    [JsonPropertyName("PatientID")]
    public string PatientID { get; set; } = string.Empty;
    
    [JsonPropertyName("AccessionNumber")]
    public string? AccessionNumber { get; set; }
    
    [JsonPropertyName("PatientSex")]
    public string? PatientSex { get; set; }
    
    [JsonPropertyName("series")]
    public List<ViewerSeries> Series { get; set; } = new();
}

public class ViewerSeries
{
    [JsonPropertyName("SeriesInstanceUID")]
    public string SeriesInstanceUID { get; set; } = string.Empty;
    
    [JsonPropertyName("SeriesNumber")]
    public string? SeriesNumber { get; set; }
    
    [JsonPropertyName("Modality")]
    public string? Modality { get; set; }
    
    [JsonPropertyName("SliceThickness")]
    public decimal SliceThickness { get; set; }
    
    [JsonPropertyName("instances")]
    public List<ViewerInstance> Instances { get; set; } = new();
}

public class ViewerInstance
{
    [JsonPropertyName("metadata")]
    public InstanceMetadata Metadata { get; set; } = new();
    
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}

public class InstanceMetadata
{
    [JsonPropertyName("Columns")]
    public int Columns { get; set; }
    
    [JsonPropertyName("Rows")]
    public int Rows { get; set; }
    
    [JsonPropertyName("InstanceNumber")]
    public string? InstanceNumber { get; set; }
    
    [JsonPropertyName("SOPClassUID")]
    public string SOPClassUID { get; set; } = string.Empty;
    
    [JsonPropertyName("PhotometricInterpretation")]
    public string? PhotometricInterpretation { get; set; }
    
    [JsonPropertyName("BitsAllocated")]
    public int BitsAllocated { get; set; }
    
    [JsonPropertyName("BitsStored")]
    public int BitsStored { get; set; }
    
    [JsonPropertyName("PixelRepresentation")]
    public int PixelRepresentation { get; set; }
    
    [JsonPropertyName("SamplesPerPixel")]
    public int SamplesPerPixel { get; set; }
    
    [JsonPropertyName("PixelSpacing")]
    public decimal[] PixelSpacing { get; set; } = Array.Empty<decimal>();
    
    [JsonPropertyName("HighBit")]
    public int HighBit { get; set; }
    
    [JsonPropertyName("ImageOrientationPatient")]
    public decimal[] ImageOrientationPatient { get; set; } = Array.Empty<decimal>();
    
    [JsonPropertyName("ImagePositionPatient")]
    public decimal[] ImagePositionPatient { get; set; } = Array.Empty<decimal>();
    
    [JsonPropertyName("FrameOfReferenceUID")]
    public string? FrameOfReferenceUID { get; set; }
    
    [JsonPropertyName("ImageType")]
    public string[] ImageType { get; set; } = Array.Empty<string>();
    
    [JsonPropertyName("Modality")]
    public string? Modality { get; set; }
    
    [JsonPropertyName("SOPInstanceUID")]
    public string SOPInstanceUID { get; set; } = string.Empty;
    
    [JsonPropertyName("SeriesInstanceUID")]
    public string SeriesInstanceUID { get; set; } = string.Empty;
    
    [JsonPropertyName("StudyInstanceUID")]
    public string StudyInstanceUID { get; set; } = string.Empty;
    
    [JsonPropertyName("WindowCenter")]
    public decimal WindowCenter { get; set; }
    
    [JsonPropertyName("WindowWidth")]
    public decimal WindowWidth { get; set; }
    
    [JsonPropertyName("SeriesDate")]
    public string? SeriesDate { get; set; }
} 