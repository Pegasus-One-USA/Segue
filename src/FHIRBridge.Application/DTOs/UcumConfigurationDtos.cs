namespace FHIRBridge.Application.DTOs;

public sealed record UcumConfigurationDto(
    bool SchedulerEnabled,
    string Frequency,
    string ExecutionTime);

public sealed record UpdateUcumConfigurationRequest(
    bool SchedulerEnabled,
    string Frequency,
    string ExecutionTime);
