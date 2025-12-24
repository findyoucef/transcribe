using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Transcribe;

namespace transcribe.tests
{
    [TestClass]
    public class AudioDeviceInfoTests
    {
        [TestMethod]
        public void Constructor_ShouldSetProperties()
        {
            var device = new AudioDeviceInfo(1, "Test Device", 2);
            device.Index.Should().Be(1);
            device.Name.Should().Be("Test Device");
            device.Channels.Should().Be(2);
        }

        [TestMethod]
        public void RecordEquality_ShouldWork()
        {
            var device1 = new AudioDeviceInfo(1, "Name", 2);
            var device2 = new AudioDeviceInfo(1, "Name", 2);
            device1.Should().Be(device2);
        }
    }
}
