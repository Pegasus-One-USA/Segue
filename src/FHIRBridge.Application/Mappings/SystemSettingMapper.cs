using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Mappings;

public static class SystemSettingMapper
{
    public static SystemSettingDto ToDto(SystemSetting setting) =>
        new(
            setting.Id,
            setting.Key,
            setting.Value,
            setting.Description,
            setting.CreatedOnUtc,
            setting.ModifiedOnUtc,
            setting.CreatedBy,
            setting.ModifiedBy);
}
