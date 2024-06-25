// See https://aka.ms/new-console-template for more information

using Dan.Common.Enums;
using Dan.Common.Extensions;
using Dan.Common.Models;

Console.WriteLine("Hello, World!");

var result = TryGetParameter(MakeRequest(), "test", out string? parsedValue);


static bool TryGetParameter(EvidenceHarvesterRequest ehr, string paramName, out string? value)
{
    if (!ehr.TryGetParameter(paramName, out EvidenceParameter? parameter))
    {
        value = default;
        return false;
    }

    value = (string?)parameter?.Value;
    return true;
}

static EvidenceHarvesterRequest MakeRequest()
{
    return new EvidenceHarvesterRequest()
    {
        Parameters = new List<EvidenceParameter>()
        {
            new EvidenceParameter()
            {
                EvidenceParamName = "test",
                Value = 6,
                ParamType = EvidenceParamType.String
            }
        }
    };
}