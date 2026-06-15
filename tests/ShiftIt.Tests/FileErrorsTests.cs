using ShiftIt.Services;

namespace ShiftIt.Tests;

public sealed class FileErrorsTests
{
    // Win32 error code -> IOException HResult (0x8007____).
    private static IOException IoWith(int win32Code) =>
        new("simulated", unchecked((int)(0x80070000 | (uint)win32Code)));

    [Theory]
    [InlineData(112)] // ERROR_DISK_FULL
    [InlineData(39)]  // ERROR_HANDLE_DISK_FULL
    public void Classify_DiskFull(int code) =>
        Assert.Equal(FileErrorKind.DiskFull, FileErrors.Classify(IoWith(code)));

    [Theory]
    [InlineData(32)]   // ERROR_SHARING_VIOLATION
    [InlineData(33)]   // ERROR_LOCK_VIOLATION
    [InlineData(64)]   // ERROR_NETNAME_DELETED
    [InlineData(53)]   // ERROR_BAD_NETPATH
    [InlineData(1117)] // ERROR_IO_DEVICE
    public void Classify_Transient(int code) =>
        Assert.Equal(FileErrorKind.Transient, FileErrors.Classify(IoWith(code)));

    [Theory]
    [InlineData(2)]   // file not found
    [InlineData(1)]   // invalid function
    public void Classify_Permanent_ForUnknownIoCodes(int code) =>
        Assert.Equal(FileErrorKind.Permanent, FileErrors.Classify(IoWith(code)));

    [Fact]
    public void Classify_Permission_ForUnauthorizedAccess() =>
        Assert.Equal(FileErrorKind.Permission, FileErrors.Classify(new UnauthorizedAccessException()));

    [Fact]
    public void Classify_Inaccessible_ForMissingDrive() =>
        Assert.Equal(FileErrorKind.Inaccessible, FileErrors.Classify(new DriveNotFoundException()));

    [Fact]
    public void Classify_Inaccessible_ForMissingDirectory() =>
        Assert.Equal(FileErrorKind.Inaccessible, FileErrors.Classify(new DirectoryNotFoundException()));

    [Fact]
    public void Classify_Permanent_ForUnrelatedException() =>
        Assert.Equal(FileErrorKind.Permanent, FileErrors.Classify(new InvalidOperationException()));
}
