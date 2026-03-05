using System;
using System.Collections.Generic;

namespace SpeechLiveUploader.Models
{
    /// <summary>
    /// Represents a single health event to be sent to Vitalytics.
    /// </summary>
    public class VitalyticsEvent
    {
        public string Id { get; set; } = string.Empty;
        public string Timestamp { get; set; } = string.Empty;
        public string Level { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public Dictionary<string, object>? Context { get; set; }
        public string[]? StackTrace { get; set; }

        public VitalyticsEvent()
        {
            Id = $"evt-{Guid.NewGuid()}";
            Timestamp = DateTime.UtcNow.ToString("o");
        }
    }

    /// <summary>
    /// Device information for Vitalytics API.
    /// </summary>
    public class VitalyticsDeviceInfo
    {
        public string DeviceId { get; set; } = string.Empty;
        public string DeviceModel { get; set; } = string.Empty;
        public string OsVersion { get; set; } = string.Empty;
        public string AppVersion { get; set; } = string.Empty;
        public string? BuildNumber { get; set; }
        public string Platform { get; set; } = "windows";
    }

    /// <summary>
    /// Request body structure for Vitalytics API POST /health/events.
    /// </summary>
    public class VitalyticsRequest
    {
        public string BatchId { get; set; } = string.Empty;
        public VitalyticsDeviceInfo DeviceInfo { get; set; } = new();
        public string AppIdentifier { get; set; } = string.Empty;
        public string Environment { get; set; } = string.Empty;
        public bool IsTest { get; set; }
        public List<VitalyticsEvent> Events { get; set; } = new();
        public string SentAt { get; set; } = string.Empty;

        public VitalyticsRequest()
        {
            BatchId = Guid.NewGuid().ToString();
            SentAt = DateTime.UtcNow.ToString("o");
        }
    }
}
