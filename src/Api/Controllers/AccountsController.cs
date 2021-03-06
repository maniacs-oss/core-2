﻿using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Bit.Core.Models.Api;
using Bit.Core.Exceptions;
using Bit.Core.Services;
using Microsoft.AspNetCore.Identity;
using Bit.Core.Models.Table;
using Bit.Core.Enums;
using System.Linq;
using Bit.Core.Repositories;
using System.Collections;

namespace Bit.Api.Controllers
{
    [Route("accounts")]
    [Authorize("Application")]
    public class AccountsController : Controller
    {
        private readonly IUserService _userService;
        private readonly ICipherService _cipherService;
        private readonly IOrganizationUserRepository _organizationUserRepository;
        private readonly UserManager<User> _userManager;

        public AccountsController(
            IUserService userService,
            ICipherService cipherService,
            IOrganizationUserRepository organizationUserRepository,
            UserManager<User> userManager)
        {
            _userService = userService;
            _cipherService = cipherService;
            _organizationUserRepository = organizationUserRepository;
            _userManager = userManager;
        }

        [HttpPost("register")]
        [AllowAnonymous]
        public async Task PostRegister([FromBody]RegisterRequestModel model)
        {
            var result = await _userService.RegisterUserAsync(model.ToUser(), model.MasterPasswordHash);
            if(result.Succeeded)
            {
                return;
            }

            foreach(var error in result.Errors.Where(e => e.Code != "DuplicateUserName"))
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            await Task.Delay(2000);
            throw new BadRequestException(ModelState);
        }

        [HttpPost("password-hint")]
        [AllowAnonymous]
        public async Task PostPasswordHint([FromBody]PasswordHintRequestModel model)
        {
            await _userService.SendMasterPasswordHintAsync(model.Email);
        }

        [HttpPost("email-token")]
        public async Task PostEmailToken([FromBody]EmailTokenRequestModel model)
        {
            var user = await _userService.GetUserByPrincipalAsync(User);
            if(!await _userManager.CheckPasswordAsync(user, model.MasterPasswordHash))
            {
                await Task.Delay(2000);
                throw new BadRequestException("MasterPasswordHash", "Invalid password.");
            }

            await _userService.InitiateEmailChangeAsync(user, model.NewEmail);
        }

        [HttpPut("email")]
        [HttpPost("email")]
        public async Task PutEmail([FromBody]EmailRequestModel model)
        {
            var user = await _userService.GetUserByPrincipalAsync(User);

            // NOTE: It is assumed that the eventual repository call will make sure the updated
            // ciphers belong to user making this call. Therefore, no check is done here.
            var ciphers = model.Ciphers.Select(c => c.ToCipher(user.Id));

            var result = await _userService.ChangeEmailAsync(
                user,
                model.MasterPasswordHash,
                model.NewEmail,
                model.NewMasterPasswordHash,
                model.Token,
                ciphers);

            if(result.Succeeded)
            {
                return;
            }

            foreach(var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            await Task.Delay(2000);
            throw new BadRequestException(ModelState);
        }

        [HttpPut("password")]
        [HttpPost("password")]
        public async Task PutPassword([FromBody]PasswordRequestModel model)
        {
            var user = await _userService.GetUserByPrincipalAsync(User);

            // NOTE: It is assumed that the eventual repository call will make sure the updated
            // ciphers belong to user making this call. Therefore, no check is done here.
            var ciphers = model.Ciphers.Select(c => c.ToCipher(user.Id));

            var result = await _userService.ChangePasswordAsync(
                user,
                model.MasterPasswordHash,
                model.NewMasterPasswordHash,
                ciphers);

            if(result.Succeeded)
            {
                return;
            }

            foreach(var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            await Task.Delay(2000);
            throw new BadRequestException(ModelState);
        }

        [HttpPut("security-stamp")]
        [HttpPost("security-stamp")]
        public async Task PutSecurityStamp([FromBody]SecurityStampRequestModel model)
        {
            var user = await _userService.GetUserByPrincipalAsync(User);
            var result = await _userService.RefreshSecurityStampAsync(user, model.MasterPasswordHash);
            if(result.Succeeded)
            {
                return;
            }

            foreach(var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            await Task.Delay(2000);
            throw new BadRequestException(ModelState);
        }

        [HttpGet("profile")]
        public async Task<ProfileResponseModel> GetProfile()
        {
            var user = await _userService.GetUserByPrincipalAsync(User);
            var organizationUserDetails = await _organizationUserRepository.GetManyDetailsByUserAsync(user.Id,
                OrganizationUserStatusType.Confirmed);
            var response = new ProfileResponseModel(user, organizationUserDetails);
            return response;
        }

        [HttpGet("organizations")]
        public async Task<ListResponseModel<ProfileOrganizationResponseModel>> GetOrganizations()
        {
            var userId = _userService.GetProperUserId(User);
            var organizationUserDetails = await _organizationUserRepository.GetManyDetailsByUserAsync(userId.Value,
                OrganizationUserStatusType.Confirmed);
            var responseData = organizationUserDetails.Select(o => new ProfileOrganizationResponseModel(o));
            return new ListResponseModel<ProfileOrganizationResponseModel>(responseData);
        }

        [HttpPut("profile")]
        [HttpPost("profile")]
        public async Task<ProfileResponseModel> PutProfile([FromBody]UpdateProfileRequestModel model)
        {
            var user = await _userService.GetUserByPrincipalAsync(User);
            await _userService.SaveUserAsync(model.ToUser(user));
            var response = new ProfileResponseModel(user, null);
            return response;
        }

        [HttpGet("revision-date")]
        public async Task<long?> GetAccountRevisionDate()
        {
            var userId = _userService.GetProperUserId(User);
            long? revisionDate = null;
            if(userId.HasValue)
            {
                var date = await _userService.GetAccountRevisionDateByIdAsync(userId.Value);
                revisionDate = Core.Utilities.CoreHelpers.ToEpocMilliseconds(date);
            }

            return revisionDate;
        }

        [HttpGet("two-factor")]
        public async Task<TwoFactorResponseModel> GetTwoFactor(string masterPasswordHash, TwoFactorProviderType provider)
        {
            var user = await _userService.GetUserByPrincipalAsync(User);
            if(!await _userManager.CheckPasswordAsync(user, masterPasswordHash))
            {
                await Task.Delay(2000);
                throw new BadRequestException("MasterPasswordHash", "Invalid password.");
            }

            await _userService.GetTwoFactorAsync(user, provider);

            var response = new TwoFactorResponseModel(user);
            return response;
        }

        [HttpPut("two-factor")]
        [HttpPost("two-factor")]
        public async Task<TwoFactorResponseModel> PutTwoFactor([FromBody]UpdateTwoFactorRequestModel model)
        {
            var user = await _userService.GetUserByPrincipalAsync(User);
            if(!await _userManager.CheckPasswordAsync(user, model.MasterPasswordHash))
            {
                await Task.Delay(2000);
                throw new BadRequestException("MasterPasswordHash", "Invalid password.");
            }

            if(!await _userManager.VerifyTwoFactorTokenAsync(user, TwoFactorProviderType.Authenticator.ToString(), model.Token))
            {
                await Task.Delay(2000);
                throw new BadRequestException("Token", "Invalid token.");
            }

            user.TwoFactorProvider = TwoFactorProviderType.Authenticator;
            user.TwoFactorEnabled = model.Enabled.Value;
            user.TwoFactorRecoveryCode = user.TwoFactorEnabled ? Guid.NewGuid().ToString("N") : null;
            await _userService.SaveUserAsync(user);

            var response = new TwoFactorResponseModel(user);
            return response;
        }

        [HttpPost("two-factor-recover")]
        [AllowAnonymous]
        public async Task PostTwoFactorRecover([FromBody]RecoverTwoFactorRequestModel model)
        {
            if(!await _userService.RecoverTwoFactorAsync(model.Email, model.MasterPasswordHash, model.RecoveryCode))
            {
                await Task.Delay(2000);
                throw new BadRequestException(string.Empty, "Invalid information. Try again.");
            }
        }

        [HttpPut("two-factor-regenerate")]
        [HttpPost("two-factor-regenerate")]
        public async Task<TwoFactorResponseModel> PutTwoFactorRegenerate([FromBody]RegenerateTwoFactorRequestModel model)
        {
            var user = await _userService.GetUserByPrincipalAsync(User);
            if(!await _userManager.CheckPasswordAsync(user, model.MasterPasswordHash))
            {
                await Task.Delay(2000);
                throw new BadRequestException("MasterPasswordHash", "Invalid password.");
            }

            if(!await _userManager.VerifyTwoFactorTokenAsync(user, TwoFactorProviderType.Authenticator.ToString(), model.Token))
            {
                await Task.Delay(2000);
                throw new BadRequestException("Token", "Invalid token.");
            }

            if(user.TwoFactorEnabled)
            {
                user.TwoFactorRecoveryCode = Guid.NewGuid().ToString("N");
                await _userService.SaveUserAsync(user);
            }

            var response = new TwoFactorResponseModel(user);
            return response;
        }

        [HttpPut("keys")]
        [HttpPost("keys")]
        public async Task<KeysResponseModel> PutKeys([FromBody]KeysRequestModel model)
        {
            var user = await _userService.GetUserByPrincipalAsync(User);
            await _userService.SaveUserAsync(model.ToUser(user));
            return new KeysResponseModel(user);
        }

        [HttpGet("keys")]
        public async Task<KeysResponseModel> GetKeys()
        {
            var user = await _userService.GetUserByPrincipalAsync(User);
            return new KeysResponseModel(user);
        }

        [HttpPost("delete")]
        public async Task PostDelete([FromBody]DeleteAccountRequestModel model)
        {
            var user = await _userService.GetUserByPrincipalAsync(User);
            if(!await _userManager.CheckPasswordAsync(user, model.MasterPasswordHash))
            {
                ModelState.AddModelError("MasterPasswordHash", "Invalid password.");
                await Task.Delay(2000);
            }
            else
            {
                var result = await _userService.DeleteAsync(user);
                if(result.Succeeded)
                {
                    return;
                }

                foreach(var error in result.Errors)
                {
                    ModelState.AddModelError(string.Empty, error.Description);
                }
            }

            throw new BadRequestException(ModelState);
        }
    }
}
