using DiskApp.Core.Results;

namespace DiskApp.Core.Tests.Results;

/// <summary>
/// Результат операции: успех и ошибка, доступ к значению, преобразования, поведение значения по умолчанию.
/// </summary>
public sealed class ResultTests
{
    private static readonly DomainError NotFound = new("test.not_found", "Не найдено");

    [Fact]
    public void Ok_Value_IsSuccessWithValue()
    {
        // Act
        var result = Result.Ok(42);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.IsFailure.ShouldBeFalse();
        result.Value.ShouldBe(42);
        result.Error.ShouldBeNull();
        result.TryGetValue(out var value).ShouldBeTrue();
        value.ShouldBe(42);
    }

    [Fact]
    public void Fail_Error_IsFailureAndValueThrows()
    {
        // Act
        var result = Result.Fail<int>(NotFound);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(NotFound);
        result.TryGetValue(out _).ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => result.Value).Message.ShouldContain("test.not_found");
    }

    [Fact]
    public void Default_Result_IsFailureWithUninitializedError()
    {
        // Arrange
        Result<string> result = default;

        // Act & Assert
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(DomainError.Uninitialized);
    }

    [Fact]
    public void Map_Success_TransformsValue()
    {
        // Act
        var mapped = Result.Ok(21).Map(static value => value * 2);

        // Assert
        mapped.Value.ShouldBe(42);
    }

    [Fact]
    public void Map_Failure_KeepsError()
    {
        // Act
        var mapped = Result.Fail<int>(NotFound).Map(static value => value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // Assert
        mapped.IsFailure.ShouldBeTrue();
        mapped.Error.ShouldBe(NotFound);
    }

    [Fact]
    public void Match_BothBranches_ReturnSelectedValue()
    {
        // Act
        var success = Result.Ok("a").Match(static value => value + "!", static error => error.Code);
        var failure = Result.Fail<string>(NotFound).Match(static value => value + "!", static error => error.Code);

        // Assert
        success.ShouldBe("a!");
        failure.ShouldBe("test.not_found");
    }

    [Fact]
    public void ToString_SuccessAndFailure_DescribeState()
    {
        // Act & Assert
        Result.Ok("x").ToString().ShouldBe("Ok(x)");
        Result.Fail<string>(NotFound).ToString().ShouldBe("Fail(test.not_found: Не найдено)");
    }
}
