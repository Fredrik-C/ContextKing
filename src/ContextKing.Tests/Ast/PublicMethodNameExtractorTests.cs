using ContextKing.Core.Ast;
using FluentAssertions;

namespace ContextKing.Tests.Ast;

public class PublicMethodNameExtractorTests
{
    [Fact]
    public void Extract_PublicSurface_IncludesTypesPropertiesConstructorsAndEnumMembers()
    {
        var source = """
            namespace Payments;

            public enum PaymentType
            {
                Sale,
                Refund
            }

            public record PaymentData(string PaymentType, string TransactionId);

            public class PaymentRequest
            {
                public PaymentRequest(string id) { }
                public string SaleData { get; set; }
                public void RequestTerminalAuthorizationAsync() { }
                private void InternalHelper() { }
            }

            internal class InternalDto
            {
                public string Hidden { get; set; }
            }
            """;

        var names = PublicMethodNameExtractor.Extract(source);

        names.Should().Contain([
            "PaymentType",
            "Sale",
            "Refund",
            "PaymentData",
            "PaymentRequest",
            "TransactionId",
            "SaleData",
            "RequestTerminalAuthorizationAsync"
        ]);
        names.Should().NotContain("InternalHelper");
        names.Should().NotContain("InternalDto");
        names.Should().NotContain("Hidden");
    }

    [Fact]
    public void Extract_InterfaceMembers_AreIncluded()
    {
        var source = """
            public interface ITerminalGateway
            {
                string TerminalId { get; }
                void RefundPaymentAsync();
            }
            """;

        var names = PublicMethodNameExtractor.Extract(source);

        names.Should().Contain(["ITerminalGateway", "TerminalId", "RefundPaymentAsync"]);
    }
}
