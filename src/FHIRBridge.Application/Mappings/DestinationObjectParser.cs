namespace FHIRBridge.Application.Mappings;

/// <summary>
/// Strips the ';mode=…'/'?key=…' suffixes a stored <c>DestinationObject</c> (e.g. from
/// <c>RelationalDestinationWriterBase</c>/<c>MappedSqlServerDestinationWriter</c>) can carry, leaving just the
/// 'Table' or 'Schema.Table' identifier — shared by anything that needs to match a mapping's destination object
/// against introspected schema (<c>DestinationTableSchemaDto.FullName</c>/<c>TableName</c>).
/// </summary>
public static class DestinationObjectParser
{
    public static string ParseTableName(string destinationObject)
    {
        var name = destinationObject.Trim().Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? destinationObject;

        var queryIndex = name.IndexOf('?', StringComparison.Ordinal);
        return queryIndex >= 0 ? name[..queryIndex] : name;
    }
}
