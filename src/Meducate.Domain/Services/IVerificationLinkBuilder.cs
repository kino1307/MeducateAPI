namespace Meducate.Domain.Services;

internal interface IVerificationLinkBuilder
{
    string Build(string token);
}
