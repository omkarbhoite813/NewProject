using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Compression;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using XmlMiddleware.Application.Helpers;
using XmlMiddleware.Application.Interfaces;
using XmlMiddleware.Domain.Entities;
using XmlMiddleware.Domain.Enums;
using XmlMiddleware.Domain.Enums.XmlMiddleware.Domain.Enums;
using XmlMiddleware.Domain.Models;

namespace XmlMiddleware.Application.Services
{
    public class XmlProcessingService : IXmlProcessingService
    {
        private readonly IBlobStorageService _blobStorageService;
        private readonly IXmlMappingService _mappingService;
        private readonly IProcessingStateRepository _stateRepository;
        private readonly ILogger<XmlProcessingService> _logger;

        public XmlProcessingService(
            IBlobStorageService blobStorageService,
            IXmlMappingService mappingService,
            IProcessingStateRepository stateRepository,
            ILogger<XmlProcessingService> logger)
        {
            _blobStorageService = blobStorageService;
            _mappingService = mappingService;
            _stateRepository = stateRepository;
            _logger = logger;
        }



        public CustomersXmlModel Deserialize(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml))
            {
                throw new InvalidDataException("XML file is empty.");
            }

            try
            {
                var serializer = new XmlSerializer(
                typeof(CustomersXmlModel));

                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null
                };

                using var stringReader = new StringReader(xml);
                using var xmlReader = XmlReader.Create(
                stringReader,
                settings);

                var result = serializer.Deserialize(xmlReader)
                as CustomersXmlModel;

                return result
                ?? throw new InvalidDataException(
                "XML could not be deserialized.");
            }
            catch (InvalidOperationException exception)
            {
                throw new InvalidDataException(
                "Invalid XML structure.",
                exception);
            }
            catch (XmlException exception)
            {
                throw new InvalidDataException(
                "XML is not well-formed.",
                exception);
            }
        }

        public void Validate(CustomersXmlModel document)
        {
            if (document.Customers.Count == 0)
            {
                throw new InvalidDataException(
                "The XML must contain at least one customer.");
            }

            var customerIds = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

            for (var index = 0;
            index < document.Customers.Count;
            index++)
            {
                var customer = document.Customers[index];
                var recordNumber = index + 1;

                ValidateRequired(
                customer.CustomerId,
                "CustomerId",
                recordNumber);

                ValidateRequired(
                customer.FirstName,
                "FirstName",
                recordNumber);

                ValidateRequired(
                customer.LastName,
                "LastName",
                recordNumber);

                ValidateRequired(
                customer.DateOfBirth,
                "DateOfBirth",
                recordNumber);

                ValidateRequired(
                customer.Email,
                "Email",
                recordNumber);

                ValidateRequired(
                customer.Phone,
                "Phone",
                recordNumber);

                if (customer.Address is null)
                {
                    throw new InvalidDataException(
                    $"Record {recordNumber}: Address is required.");
                }

                ValidateRequired(
                customer.Address.AddressLine1,
                "AddressLine1",
                recordNumber);

                ValidateRequired(
                customer.Address.City,
                "City",
                recordNumber);

                ValidateRequired(
                customer.Address.PostCode,
                "PostCode",
                recordNumber);

                ValidateRequired(
                customer.Address.Country,
                "Country",
                recordNumber);

                ValidateDate(
                customer.DateOfBirth!,
                recordNumber);

                ValidateEmail(customer.Email!, recordNumber);

                if (!customerIds.Add(customer.CustomerId!))
                {
                    throw new InvalidDataException(
                    $"Record {recordNumber}: Duplicate CustomerId " +
                    $"{customer.CustomerId}.");
                }
            }
        }


        private static void ValidateRequired(string? value, string fieldName,int recordNumber)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidDataException(
                $"Record {recordNumber}: " +
                $"{fieldName} is mandatory.");
            }
        }

        private static void ValidateDate(string value,int recordNumber)
        {
            var isValid = DateTime.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var date);

            if (!isValid)
            {
                throw new InvalidDataException(
                $"Record {recordNumber}: " +
                "DateOfBirth must use yyyy-MM-dd.");
            }

            if (date.Date > DateTime.UtcNow.Date)
            {
                throw new InvalidDataException(
                $"Record {recordNumber}: " +
                "DateOfBirth cannot be in the future.");
            }
        }

        private static void ValidateEmail(string value, int recordNumber)
        {
            try
            {
                var email = value.Trim();
                var mailAddress = new MailAddress(email);

                var isValid =
                    email.Contains('@') &&
                    mailAddress.Host.Contains('.') &&
                    string.Equals(mailAddress.Address, email, StringComparison.OrdinalIgnoreCase);

                if (!isValid)
                {
                    throw new FormatException();
                }
            }
            catch (FormatException)
            {
                throw new InvalidDataException(
                    $"Record {recordNumber}: Email has an invalid format.");
            }
        }


        public async Task ProcessAsync( string fileName,byte[] fileBytes,CancellationToken cancellationToken = default)
        {
            // workflow here
            
            //Calculate SHA-256 hash.
            var hashBytes = SHA256.HashData(fileBytes);
            var fileHash = Convert.ToHexString(hashBytes);
            // Check whether identical XML content was already completed.
            var isDuplicate = await _stateRepository.IsDuplicateAsync(fileHash, cancellationToken);
            if (isDuplicate)
            {
                _logger.LogWarning("Duplicate XML ignored. FileName: {FileName}, FileHash: {FileHash}", fileName, fileHash);

                await _blobStorageService.DeleteInputAsync(fileName, cancellationToken);

                _logger.LogInformation("Duplicate XML deleted from input. FileName: {FileName}", fileName);

                return;
            }
            //=====================================================================================================================================

            //====Generate GUID for stateID and transactionId  AND Checks if any prosess is in retry state 
            var existingState = await _stateRepository.GetPendingByHashAsync(fileHash, cancellationToken);

            var stateId = existingState?.Id ?? Guid.NewGuid();
            var transactionId = existingState?.TransactionId ?? Guid.NewGuid().ToString();
            var stateCreated = existingState is not null;

            ProcessingState? processingState = null;

            if (existingState is null)
            {
                var currentTime = DateTime.UtcNow;

                processingState = new ProcessingState
                {
                    Id = stateId,
                    TransactionId = transactionId,
                    InputFileName = fileName,
                    OutputFileName = null,
                    FileHash = fileHash,
                    Status = ProcessingStatus.Received,
                    RecordCount = 0,
                    RetryCount = 0,
                    StartedAt = currentTime,
                    CompletedAt = null,
                    ErrorCode = null,
                    ErrorMessage = null,
                    CreatedAt = currentTime,
                    UpdatedAt = currentTime
                };

                _logger.LogInformation("New processing transaction started. TransactionId: {TransactionId}, FileName: {FileName}", transactionId, fileName);
            }
            else
            {
                _logger.LogInformation("Retrying existing transaction. TransactionId: {TransactionId}, FileName: {FileName}, RetryCount: {RetryCount}", transactionId, fileName, existingState.RetryCount);
            }

            _logger.LogInformation("Processing started. TransactionId: {TransactionId}, FileName: {FileName}", transactionId, fileName);

            try
            {
                if (processingState is not null)
                {
                    await _stateRepository.AddAsync(processingState, cancellationToken);

                    stateCreated = true;

                    _logger.LogInformation("Processing state created with RECEIVED status. TransactionId: {TransactionId}", transactionId);
                }



                await _stateRepository.UpdateStatusAsync(stateId, ProcessingStatus.Processing, cancellationToken);

                _logger.LogInformation("Status updated to PROCESSING. TransactionId: {TransactionId}", transactionId);



                //========================================================================================================
                //Simulate Retry method 
                var simulateTransientFailure = string.Equals(Environment.GetEnvironmentVariable("SimulateTransientFailure"), "true", StringComparison.OrdinalIgnoreCase);

                var currentRetryCount = existingState?.RetryCount ?? 0;

                if (simulateTransientFailure && currentRetryCount == 0)
                {
                    _logger.LogWarning("Simulating transient timeout for retry testing. TransactionId: {TransactionId}, RetryCount: {RetryCount}", transactionId, currentRetryCount);

                    // throw new TimeoutException("Simulated temporary timeout for retry testing.");  //-- Exception throe 
                }
                //========================================================================================================

                //====================================
                // For Hash - // Use the bytes that were already read.
                //-----------------------------------

                var xmlContent = Encoding.UTF8.GetString(fileBytes).TrimStart('\uFEFF');

                var document = Deserialize(xmlContent);
                //-------------------------------

                Validate(document);

                _logger.LogInformation("XML successfully validated. TransactionId: {TransactionId}", transactionId);
                _logger.LogInformation("FileName: {FileName}", fileName);
                _logger.LogInformation("RecordCount: {RecordCount}", document.Customers.Count);

                foreach (var customer in document.Customers)
                {
                    _logger.LogInformation("Customer found. CustomerId: {CustomerId}, TransactionId: {TransactionId}", customer.CustomerId, transactionId);
                }

                var txtContent = _mappingService.MapToTxt(document);

                _logger.LogInformation("TXT generated. CharacterCount: {CharacterCount}, TransactionId: {TransactionId}", txtContent.Length, transactionId);

                var outputFileName = Path.ChangeExtension(fileName, ".txt");

                await _blobStorageService.UploadTxtAsync(outputFileName, txtContent, cancellationToken);

                _logger.LogInformation("TXT uploaded successfully. OutputFileName: {OutputFileName}, TransactionId: {TransactionId}", outputFileName, transactionId);

                //------------------------------------------------------------------------------------------
                //Successful completion move to Archive 
                using var archiveStream = new MemoryStream(fileBytes);

                await _blobStorageService.MoveToArchiveAsync(fileName, archiveStream, cancellationToken);

                _logger.LogInformation("XML copied to archive. TransactionId: {TransactionId}, FileName: {FileName}", transactionId, fileName);

                await _stateRepository.CompleteAsync(stateId, outputFileName, document.Customers.Count, cancellationToken);

                _logger.LogInformation("Status updated to COMPLETED. TransactionId: {TransactionId}, FileName: {FileName}", transactionId, fileName);

                try
                {
                    await _blobStorageService.DeleteInputAsync(fileName, cancellationToken);

                    _logger.LogInformation("Original XML deleted from input. TransactionId: {TransactionId}, FileName: {FileName}", transactionId, fileName);
                }
                catch (Exception deleteException)
                {
                    _logger.LogError(deleteException, "Processing completed, but input XML could not be deleted. TransactionId: {TransactionId}, FileName: {FileName}", transactionId, fileName);
                }


                //---------------------------------------------------------------------------------------
            }
            catch (InvalidDataException exception)
            {
                //====================================================================================
                //Validation Failed move to Archive 
                _logger.LogWarning(exception, "XML validation failed. TransactionId: {TransactionId}, FileName: {FileName}", transactionId, fileName);

                if (stateCreated)
                {
                    try
                    {
                        await _stateRepository.FailAsync(stateId, "XML_VALIDATION_FAILED", exception.Message, cancellationToken);

                        _logger.LogInformation("Status updated to FAILED. TransactionId: {TransactionId}", transactionId);
                    }
                    catch (Exception stateUpdateException)
                    {
                        _logger.LogCritical(stateUpdateException, "Unable to update validation failure status. TransactionId: {TransactionId}", transactionId);
                    }
                }

                try
                {
                    using var errorStream = new MemoryStream(fileBytes);

                    await _blobStorageService.MoveToErrorAsync(fileName, errorStream, cancellationToken);

                    _logger.LogInformation("Invalid XML copied to error. TransactionId: {TransactionId}, FileName: {FileName}", transactionId, fileName);

                    await _blobStorageService.DeleteInputAsync(fileName, cancellationToken);

                    _logger.LogInformation("Invalid XML deleted from input. TransactionId: {TransactionId}, FileName: {FileName}", transactionId, fileName);
                }
                catch (Exception storageException)
                {
                    _logger.LogError(storageException, "Unable to move invalid XML to error. TransactionId: {TransactionId}, FileName: {FileName}", transactionId, fileName);
                }

                return;
            }

            catch (Exception exception)
            {
                var isTransient = TransientErrorHelper.IsTransient(exception);

                _logger.LogError(exception, "Unexpected processing failure. TransactionId: {TransactionId}, FileName: {FileName}, IsTransient: {IsTransient}", transactionId, fileName, isTransient);

                if (stateCreated)
                {
                    try
                    {
                        if (isTransient)
                        {
                            await _stateRepository.MarkRetryAsync(stateId, "TRANSIENT_FAILURE", exception.Message, cancellationToken);

                            _logger.LogWarning("Processing marked for retry. TransactionId: {TransactionId}, FileName: {FileName}", transactionId, fileName);
                        }
                        else
                        {
                            await _stateRepository.FailAsync(stateId, "PROCESSING_FAILED", exception.Message, cancellationToken);

                            _logger.LogError("Processing marked as FAILED. TransactionId: {TransactionId}, FileName: {FileName}", transactionId, fileName);
                        }
                    }
                    catch (Exception stateUpdateException)
                    {
                        _logger.LogCritical(stateUpdateException, "Unable to update processing state after failure. TransactionId: {TransactionId}", transactionId);
                    }
                }

                if (isTransient)
                {
                    //await _stateRepository.MarkRetryAsync( stateId,"TRANSIENT_FAILURE",exception.Message,cancellationToken);
                    throw;
                    //return;
                }

                return;
            }
        }

    }
}
