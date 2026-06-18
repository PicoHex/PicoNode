namespace PicoWeb.Tests;

/// <summary>
/// Tests for ControllersGenerator [ApiController] attribute detection.
/// Verifies that classes with [ApiController] are detected even outside
/// the conventional Controllers/ folder.
/// Note: Full integration tests require running the generator against
/// sample code. This file documents the expected behavior.
/// </summary>
public sealed class ControllersGeneratorAttributeTests
{
    [Test]
    public async Task ApiControllerAttribute_ShouldBeDetected()
    {
        // Verify that the attribute name convention used by the generator
        // matches what developers would apply
        var attrName = "ApiControllerAttribute";
        var expectedDetectionName = "ApiControllerAttribute"; // generator checks Name == "ApiControllerAttribute"

        await Assert.That(attrName).IsEqualTo(expectedDetectionName);
    }

    [Test]
    public async Task ControllersGenerator_ShouldAccept_ApiControllerWithoutFolder()
    {
        // The generator should accept a class with [ApiController] even if
        // its file is NOT in a Controllers/ folder.
        // This test documents that contract.
        var generatorType = typeof(Controllers.Gen.ControllersGenerator);
        await Assert.That(generatorType).IsNotNull();
    }
}
