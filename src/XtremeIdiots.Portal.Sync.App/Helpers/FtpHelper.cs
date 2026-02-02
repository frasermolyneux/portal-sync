using FluentFTP;

using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Extensions.Configuration;

namespace XtremeIdiots.Portal.Sync.App.Helpers;

public class FtpHelper(
    TelemetryClient telemetryClient,
    IConfiguration configuration) : IFtpHelper
{
    public async Task<long?> GetFileSize(string hostname, int port, string filePath, string username, string password, Dictionary<string, string> telemetryProperties)
    {
        var operation = telemetryClient.StartOperation<DependencyTelemetry>("GetFileSize");
        operation.Telemetry.Type = "FTP";
        operation.Telemetry.Target = $"{hostname}:{port}";
        operation.Telemetry.Data = filePath;

        foreach (var telemetryProperty in telemetryProperties)
            operation.Telemetry.Properties.Add(telemetryProperty.Key, telemetryProperty.Value);

        AsyncFtpClient? ftpClient = null;

        try
        {
            ftpClient = new AsyncFtpClient(hostname, username, password, port);
            ftpClient.ValidateCertificate += (control, e) =>
            {
                if (e.Certificate.GetCertHashString().Equals(configuration["xtremeidiots_ftp_certificate_thumbprint"]))
                { // Account for self-signed FTP certificate for self-hosted servers
                    e.Accept = true;
                }
            };

            await ftpClient.AutoConnect().ConfigureAwait(false);

            if (await ftpClient.FileExists(filePath).ConfigureAwait(false))
                return await ftpClient.GetFileSize(filePath).ConfigureAwait(false);
            else
                return null;
        }
        catch (Exception ex)
        {
            operation.Telemetry.Success = false;
            operation.Telemetry.ResultCode = "Failed";
            telemetryClient.TrackException(ex);
            throw;
        }
        finally
        {
            telemetryClient.StopOperation(operation);
            ftpClient?.Dispose();
        }
    }

    public async Task<DateTime?> GetLastModified(string hostname, int port, string filePath, string username, string password, Dictionary<string, string> telemetryProperties)
    {
        var operation = telemetryClient.StartOperation<DependencyTelemetry>("GetLastModified");
        operation.Telemetry.Type = "FTP";
        operation.Telemetry.Target = $"{hostname}:{port}";
        operation.Telemetry.Data = filePath;

        foreach (var telemetryProperty in telemetryProperties)
            operation.Telemetry.Properties.Add(telemetryProperty.Key, telemetryProperty.Value);

        AsyncFtpClient? ftpClient = null;

        try
        {
            ftpClient = new AsyncFtpClient(hostname, username, password, port);
            ftpClient.ValidateCertificate += (control, e) =>
            {
                if (e.Certificate.GetCertHashString().Equals(configuration["xtremeidiots_ftp_certificate_thumbprint"]))
                { // Account for self-signed FTP certificate for self-hosted servers
                    e.Accept = true;
                }
            };

            await ftpClient.AutoConnect().ConfigureAwait(false);

            if (await ftpClient.FileExists(filePath).ConfigureAwait(false))
                return await ftpClient.GetModifiedTime(filePath).ConfigureAwait(false);
            else
                return null;
        }
        catch (Exception ex)
        {
            operation.Telemetry.Success = false;
            operation.Telemetry.ResultCode = "Failed";
            telemetryClient.TrackException(ex);
            throw;
        }
        finally
        {
            telemetryClient.StopOperation(operation);
            ftpClient?.Dispose();
        }
    }

    public async Task<string> GetRemoteFileData(string hostname, int port, string filePath, string username, string password, Dictionary<string, string> telemetryProperties)
    {
        var operation = telemetryClient.StartOperation<DependencyTelemetry>("GetRemoteFileData");
        operation.Telemetry.Type = "FTP";
        operation.Telemetry.Target = $"{hostname}:{port}";
        operation.Telemetry.Data = filePath;

        foreach (var telemetryProperty in telemetryProperties)
            operation.Telemetry.Properties.Add(telemetryProperty.Key, telemetryProperty.Value);

        AsyncFtpClient? ftpClient = null;

        try
        {
            ftpClient = new AsyncFtpClient(hostname, username, password, port);
            ftpClient.ValidateCertificate += (control, e) =>
            {
                if (e.Certificate.GetCertHashString().Equals(configuration["xtremeidiots_ftp_certificate_thumbprint"]))
                { // Account for self-signed FTP certificate for self-hosted servers
                    e.Accept = true;
                }
            };

            await ftpClient.AutoConnect().ConfigureAwait(false);

            using var stream = new MemoryStream();
            await ftpClient.DownloadStream(stream, filePath).ConfigureAwait(false);

            using var streamReader = new StreamReader(stream);
            stream.Seek(0, SeekOrigin.Begin);
            return await streamReader.ReadToEndAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            operation.Telemetry.Success = false;
            operation.Telemetry.ResultCode = "Failed";
            telemetryClient.TrackException(ex);
            throw;
        }
        finally
        {
            telemetryClient.StopOperation(operation);
            ftpClient?.Dispose();
        }
    }

    public async Task UpdateRemoteFileFromStream(string hostname, int port, string filePath, string username, string password, Stream data, Dictionary<string, string> telemetryProperties)
    {
        var operation = telemetryClient.StartOperation<DependencyTelemetry>("UpdateRemoteFileFromStream");
        operation.Telemetry.Type = "FTP";
        operation.Telemetry.Target = $"{hostname}:{port}";
        operation.Telemetry.Data = filePath;

        foreach (var telemetryProperty in telemetryProperties)
            operation.Telemetry.Properties.Add(telemetryProperty.Key, telemetryProperty.Value);

        AsyncFtpClient? ftpClient = null;

        try
        {
            ftpClient = new AsyncFtpClient(hostname, username, password, port);
            ftpClient.ValidateCertificate += (control, e) =>
            {
                if (e.Certificate.GetCertHashString().Equals(configuration["xtremeidiots_ftp_certificate_thumbprint"]))
                { // Account for self-signed FTP certificate for self-hosted servers
                    e.Accept = true;
                }
            };

            await ftpClient.AutoConnect().ConfigureAwait(false);

            data.Seek(0, SeekOrigin.Begin);
            await ftpClient.UploadStream(data, filePath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            operation.Telemetry.Success = false;
            operation.Telemetry.ResultCode = "Failed";
            telemetryClient.TrackException(ex);
            throw;
        }
        finally
        {
            telemetryClient.StopOperation(operation);
            ftpClient?.Dispose();
        }
    }
}