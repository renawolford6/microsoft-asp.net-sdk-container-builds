using Microsoft.VisualBasic;

using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Microsoft.NET.Build.Containers;

public record struct Registry(Uri BaseUri)
{
    private const string DockerManifestV2 = "application/vnd.docker.distribution.manifest.v2+json";
    private const string DockerContainerV1 = "application/vnd.docker.container.image.v1+json";

    public async Task<Image> GetImageManifest(string name, string reference)
    {
        using HttpClient client = GetClient();

        var response = await client.GetAsync(new Uri(BaseUri, $"/v2/{name}/manifests/{reference}"));

        response.EnsureSuccessStatusCode();

        var s = await response.Content.ReadAsStringAsync();

        var manifest = JsonNode.Parse(s);

        if (manifest is null) throw new NotImplementedException("Got a manifest but it was null");

        if ((string?)manifest["mediaType"] != DockerManifestV2)
        {
            throw new NotImplementedException($"Do not understand the mediaType {manifest["mediaType"]}");
        }

        JsonNode? config = manifest["config"];
        Debug.Assert(config is not null);
        Debug.Assert(((string?)config["mediaType"]) == DockerContainerV1);

        string? configSha = (string?)config["digest"];
        Debug.Assert(configSha is not null);

        response = await client.GetAsync(new Uri(BaseUri, $"/v2/{name}/blobs/{configSha}"));

        JsonNode? configDoc = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Debug.Assert(configDoc is not null);
        //Debug.Assert(((string?)configDoc["mediaType"]) == DockerContainerV1);

        return new Image(manifest, configDoc, name, this);
    }

    /// <summary>
    /// Ensure a blob associated with <paramref name="name"/> from the registry is available locally.
    /// </summary>
    /// <param name="name">Name of the associated image.</param>
    /// <param name="descriptor"><see cref="Descriptor"/> that describes the blob.</param>
    /// <returns>Local path to the (decompressed) blob content.</returns>
    public async Task<string> DownloadBlob(string name, Descriptor descriptor)
    {
        string localPath = ContentStore.PathForDescriptor(descriptor);

        if (File.Exists(localPath))
        {
            // Assume file is up to date and just return it
            return localPath;
        }

        // No local copy, so download one

        using HttpClient client = GetClient();

        var response = await client.GetAsync(new Uri(BaseUri, $"/v2/{name}/blobs/{descriptor.Digest}"), HttpCompletionOption.ResponseHeadersRead);

        response.EnsureSuccessStatusCode();

        string tempTarballPath = ContentStore.GetTempFile();
        using (FileStream fs = File.Create(tempTarballPath))
        {
            using Stream responseStream = await response.Content.ReadAsStreamAsync();

            await responseStream.CopyToAsync(fs);
        }

        File.Move(tempTarballPath, localPath, overwrite: true);

        return localPath;
    }

    public async Task Push(Layer layer, string name, Action<string> logProgressMessage)
    {
        string digest = layer.Descriptor.Digest;

        using (FileStream contents = File.OpenRead(layer.BackingFile))
        {
            await UploadBlob(name, digest, contents);
        }
    }

    private readonly async Task UploadBlob(string name, string digest, Stream contents)
    {
        using HttpClient client = GetClient();

        if (await BlobAlreadyUploaded(name, digest, client))
        {
            // Already there!
            return;
        }

        HttpResponseMessage pushResponse = await client.PostAsync(new Uri(BaseUri, $"/v2/{name}/blobs/uploads/"), content: null);

        Debug.Assert(pushResponse.StatusCode == HttpStatusCode.Accepted);

        //Uri uploadUri = new(BaseUri, pushResponse.Headers.GetValues("location").Single() + $"?digest={layer.Descriptor.Digest}");
        Debug.Assert(pushResponse.Headers.Location is not null);

        UriBuilder x;
        if (pushResponse.Headers.Location.IsAbsoluteUri)
        {
            x = new UriBuilder(pushResponse.Headers.Location);
        }
        else
        {
            x = new UriBuilder(BaseUri + pushResponse.Headers.Location.OriginalString);
        }

        x.Query += $"&digest={Uri.EscapeDataString(digest)}";

        // TODO: consider chunking
        StreamContent content = new StreamContent(contents);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = contents.Length;
        HttpResponseMessage putResponse = await client.PutAsync(x.Uri, content);

        string resp = await putResponse.Content.ReadAsStringAsync();

        putResponse.EnsureSuccessStatusCode();
    }

    private readonly async Task<bool> BlobAlreadyUploaded(string name, string digest, HttpClient client)
    {
        HttpResponseMessage response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, new Uri(BaseUri, $"/v2/{name}/blobs/{digest}")));

        if (response.StatusCode == HttpStatusCode.OK)
        {
            return true;
        }

        return false;
    }

    private static HttpClient GetClient()
    {
        var clientHandler = new AuthHandshakeMessageHandler(new HttpClientHandler() { UseDefaultCredentials = true });
        HttpClient client = new(clientHandler);

        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue(DockerManifestV2));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue(DockerContainerV1));

        client.DefaultRequestHeaders.Add("User-Agent", ".NET Container Library");

        return client;
    }

    public async Task Push(Image x, string name, string? tag, string baseName, Action<string> logProgressMessage)
    {
        tag ??= "latest";

        using HttpClient client = GetClient();

        foreach (var descriptor in x.LayerDescriptors)
        {
            string digest = descriptor.Digest;
            logProgressMessage($"Uploading layer {digest} to registry");
            if (await BlobAlreadyUploaded(name, digest, client))
            {
                logProgressMessage($"Layer {digest} already existed");
                continue;
            }

            // Blob wasn't there; can we tell the server to get it from the base image?
            HttpResponseMessage pushResponse = await client.PostAsync(new Uri(BaseUri, $"/v2/{name}/blobs/uploads/?mount={digest}&from={baseName}"), content: null);

            if (pushResponse.StatusCode != HttpStatusCode.Created)
            {
                // The blob wasn't already available in another namespace, so fall back to explicitly uploading it

                if (!x.originatingRegistry.HasValue)
                {
                    throw new NotImplementedException("Need a good error for 'couldn't download a thing because no link to registry'");
                }

                // Ensure the blob is available locally
                await x.originatingRegistry.Value.DownloadBlob(x.OriginatingName, descriptor);
                logProgressMessage($"Finished uploading layer {digest} to registry");
                // Then push it to the destination registry
                await Push(Layer.FromDescriptor(descriptor), name, logProgressMessage);
            }
        }

        logProgressMessage($"Uploading config to registry");
        using (MemoryStream stringStream = new MemoryStream(Encoding.UTF8.GetBytes(x.config.ToJsonString())))
        {
            await UploadBlob(name, x.GetDigest(x.config), stringStream);
            logProgressMessage($"Uploaded config to registry");
        }

        logProgressMessage($"Uploading manifest to registry");
        HttpContent manifestUploadContent = new StringContent(x.manifest.ToJsonString());
        manifestUploadContent.Headers.ContentType = new MediaTypeHeaderValue(DockerManifestV2);
        var putResponse = await client.PutAsync(new Uri(BaseUri, $"/v2/{name}/manifests/{x.GetDigest(x.manifest)}"), manifestUploadContent);
        string putresponsestr = await putResponse.Content.ReadAsStringAsync();

        if (!putResponse.IsSuccessStatusCode)
        {
            string jsonResponse = await putResponse.Content.ReadAsStringAsync();
            throw new ContainerHttpException("Registry push failed.", putResponse.RequestMessage?.RequestUri?.ToString(), jsonResponse);
        }

        logProgressMessage($"Uploaded manifest to registry");

        logProgressMessage($"Uploading tag to registry");
        var putResponse2 = await client.PutAsync(new Uri(BaseUri, $"/v2/{name}/manifests/{tag}"), manifestUploadContent);

        if (!putResponse2.IsSuccessStatusCode)
        {
            string jsonResponse = await putResponse2.Content.ReadAsStringAsync();
            throw new ContainerHttpException("Registry push failed.", putResponse2.RequestMessage?.RequestUri?.ToString(), jsonResponse);
        }

        logProgressMessage($"Uploaded tag to registry");
    }
}
