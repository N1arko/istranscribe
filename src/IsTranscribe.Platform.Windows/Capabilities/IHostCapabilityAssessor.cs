namespace IsTranscribe.Host.Capabilities;

public interface IHostCapabilityAssessor
{
    HostCapabilitySnapshot Assess();
}
