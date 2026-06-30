using Shiron.Lib.DockerUtils.Model;
using Shiron.ComposeToNginx.Core.Certificates;
using Shiron.ComposeToNginx.Core.Labels;
using Shiron.ComposeToNginx.Core.Npm;

namespace Shiron.ComposeToNginx.Core.Planning;

/// <summary>
/// Result of splitting Compose services by how they should be handled:
/// managed by labels, unmanaged (candidates for interactive prompting),
/// skipped (no exposed ports), or rejected due to malformed labels.
/// </summary>
public sealed record ServiceCategorisation(
    IReadOnlyList<HostLabelConfig> Managed,
    IReadOnlyList<Service> UnmanagedWithPort,
    IReadOnlyList<Service> SkippedNoPort,
    IReadOnlyList<LabelConfigException> Errors
);

/// <summary>
/// Result of planning the label-driven hosts: the accumulated plans plus any
/// hard errors (e.g. unresolvable certificates) that must abort the transaction.
/// </summary>
public sealed record LabelledPlanResult(
    IReadOnlyList<PlannedHost> Planned,
    IReadOnlyList<string> Errors
);

/// <summary>
/// Coordinates the pure planning side of a <c>hosts push</c> run: categorising
/// services by label intent and accumulating label-driven proxy-host plans
/// (certificate resolution, overwrite detection). The CLI layer drives I/O and
/// interactive fallback around these pure steps.
/// </summary>
public sealed class HostPlanner(ICertificateResolver certificateResolver) {
    /// <summary>
    /// Splits <paramref name="services"/> into managed / unmanaged / skipped /
    /// errored buckets according to <paramref name="labelMode"/> and each
    /// service's <c>npm.*</c> labels.
    /// </summary>
    public ServiceCategorisation Categorise(IReadOnlyList<Service> services, LabelMode labelMode) {
        var managed = new List<HostLabelConfig>();
        var unmanagedWithPort = new List<Service>();
        var skippedNoPort = new List<Service>();
        var errors = new List<LabelConfigException>();

        foreach (var service in services) {
            if (labelMode != LabelMode.Ignore) {
                try {
                    var config = LabelConfigParser.TryParse(service);
                    if (config is not null) {
                        managed.Add(config);
                        continue;
                    }
                } catch (LabelConfigException ex) {
                    errors.Add(ex);
                    continue;
                }
            }

            if (service.Ports.Length > 0)
                unmanagedWithPort.Add(service);
            else
                skippedNoPort.Add(service);
        }

        return new ServiceCategorisation(managed, unmanagedWithPort, skippedNoPort, errors);
    }

    /// <summary>
    /// Accumulates label-driven plans from <paramref name="configs"/>: resolves
    /// certificates (via <see cref="ICertificateResolver"/>), detects existing
    /// hosts to overwrite (via <paramref name="existingIndex"/>), and assembles
    /// <see cref="PlannedHost"/>s. Any certificate that cannot be resolved is
    /// recorded as a hard error in the result instead of producing a plan.
    /// </summary>
    /// <remarks>
    /// Only <em>enabled</em> configs should be passed here; the caller is
    /// responsible for reporting disabled services.
    /// </remarks>
    public LabelledPlanResult PlanLabelled(
        IReadOnlyList<HostLabelConfig> configs,
        IReadOnlyList<NpmCertificateInfo> certificates,
        ExistingHostIndex existingIndex
    ) {
        var planned = new List<PlannedHost>();
        var errors = new List<string>();

        foreach (var cfg in configs) {
            int? certificateId = null;
            if (cfg.Ssl) {
                certificateId = cfg.Certificate is not null
                    ? certificateResolver.FindByReference(certificates, cfg.Certificate)
                    : certificateResolver.FindByDomain(certificates, cfg.Domains[0]);

                if (certificateId is null) {
                    var domainList = string.Join(", ", cfg.Domains);
                    errors.Add(cfg.Certificate is not null
                        ? $"Service '{cfg.Service}': certificate '{cfg.Certificate}' not found in NPM (checked nice-name and domain coverage for [{domainList}])."
                        : $"Service '{cfg.Service}': no certificate found covering [{domainList}]. Add one to NPM or set npm.cert.");
                    continue;
                }
            }

            var overwriteId = existingIndex.FindByDomain(cfg.Domains)?.Id;
            planned.Add(PlannedHost.FromLabelConfig(cfg, certificateId, overwriteId));
        }

        return new LabelledPlanResult(planned, errors);
    }
}
