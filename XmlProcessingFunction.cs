using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using XmlMiddleware.Application.Interfaces;
using XmlMiddleware.Infrastructure.Configuration;

namespace XmlMiddleware.Function.Functions
{
    public class XmlProcessingFunction
    {
        private readonly IXmlProcessingService _processingService;
        private readonly ILogger<XmlProcessingFunction> _logger;

        public XmlProcessingFunction(
            IXmlProcessingService processingService,
            ILogger<XmlProcessingFunction> logger)
        {
            _processingService = processingService;
            _logger = logger;
        }

        [Function("XmlProcessingFunction")]
        public async Task RunAsync([BlobTrigger(BlobStorageNames.InputTriggerPath, Connection = "StorageConnection")] Stream blobStream, string fileName, CancellationToken cancellationToken)
        {
            //==========================================
            //Placeholder Check 
            //-----------------------------------
            if (string.Equals(Path.GetFileName(fileName), BlobStorageNames.PlaceholderFileName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Placeholder file ignored. FileName: {FileName}", fileName);
                return;
            }
            if (!string.Equals(Path.GetExtension(fileName), ".xml", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Non-XML file ignored. FileName: {FileName}", fileName);
                return;
            }
            //==================================================================================================================



            //======================================
            // Code to check If hash already exists 
            //-------------------------------------
            //Read uploaded bolb 
            using var memoryStream = new MemoryStream();

            await blobStream.CopyToAsync(
                memoryStream,
                cancellationToken);

            await _processingService.ProcessAsync(
                fileName,
                memoryStream.ToArray(),
                cancellationToken);
        }
    }
}