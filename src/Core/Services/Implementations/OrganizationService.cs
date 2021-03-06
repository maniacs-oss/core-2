﻿using System;
using System.Linq;
using System.Threading.Tasks;
using Bit.Core.Repositories;
using Bit.Core.Models.Business;
using Bit.Core.Models.Table;
using Bit.Core.Utilities;
using Bit.Core.Exceptions;
using System.Collections.Generic;
using Microsoft.AspNetCore.DataProtection;

namespace Bit.Core.Services
{
    public class OrganizationService : IOrganizationService
    {
        private readonly IOrganizationRepository _organizationRepository;
        private readonly IOrganizationUserRepository _organizationUserRepository;
        private readonly ISubvaultRepository _subvaultRepository;
        private readonly ISubvaultUserRepository _subvaultUserRepository;
        private readonly IUserRepository _userRepository;
        private readonly IDataProtector _dataProtector;
        private readonly IMailService _mailService;

        public OrganizationService(
            IOrganizationRepository organizationRepository,
            IOrganizationUserRepository organizationUserRepository,
            ISubvaultRepository subvaultRepository,
            ISubvaultUserRepository subvaultUserRepository,
            IUserRepository userRepository,
            IDataProtectionProvider dataProtectionProvider,
            IMailService mailService)
        {
            _organizationRepository = organizationRepository;
            _organizationUserRepository = organizationUserRepository;
            _subvaultRepository = subvaultRepository;
            _subvaultUserRepository = subvaultUserRepository;
            _userRepository = userRepository;
            _dataProtector = dataProtectionProvider.CreateProtector("OrganizationServiceDataProtector");
            _mailService = mailService;
        }

        public async Task<Tuple<Organization, OrganizationUser>> SignUpAsync(OrganizationSignup signup)
        {
            var plan = StaticStore.Plans.FirstOrDefault(p => p.Type == signup.Plan);
            if(plan == null)
            {
                throw new BadRequestException("Plan not found.");
            }

            var organization = new Organization
            {
                Name = signup.Name,
                UserId = signup.Owner.Id,
                PlanType = plan.Type,
                MaxUsers = plan.MaxUsers,
                PlanTrial = plan.Trial.HasValue,
                PlanPrice = plan.Trial.HasValue ? 0 : plan.Price,
                PlanRenewalPrice = plan.Price,
                Plan = plan.ToString(),
                CreationDate = DateTime.UtcNow,
                RevisionDate = DateTime.UtcNow
            };

            if(plan.Trial.HasValue)
            {
                organization.PlanRenewalDate = DateTime.UtcNow.Add(plan.Trial.Value);
            }
            else if(plan.Cycle != null)
            {
                organization.PlanRenewalDate = DateTime.UtcNow.Add(plan.Cycle(DateTime.UtcNow));
            }

            await _organizationRepository.CreateAsync(organization);

            try
            {
                var orgUser = new OrganizationUser
                {
                    OrganizationId = organization.Id,
                    UserId = signup.Owner.Id,
                    Email = signup.Owner.Email,
                    Key = signup.OwnerKey,
                    Type = Enums.OrganizationUserType.Owner,
                    Status = Enums.OrganizationUserStatusType.Confirmed,
                    CreationDate = DateTime.UtcNow,
                    RevisionDate = DateTime.UtcNow
                };

                await _organizationUserRepository.CreateAsync(orgUser);

                return new Tuple<Organization, OrganizationUser>(organization, orgUser);
            }
            catch
            {
                await _organizationRepository.DeleteAsync(organization);
                throw;
            }
        }

        public async Task<OrganizationUser> InviteUserAsync(Guid organizationId, Guid invitingUserId, string email,
            Enums.OrganizationUserType type, IEnumerable<SubvaultUser> subvaults)
        {
            if(!(await OrganizationUserHasAdminRightsAsync(organizationId, invitingUserId)))
            {
                throw new BadRequestException("Cannot invite users.");
            }

            // Make sure user is not already invited
            var existingOrgUser = await _organizationUserRepository.GetByOrganizationAsync(organizationId, email);
            if(existingOrgUser != null)
            {
                throw new BadRequestException("User already invited.");
            }

            var orgSubvaults = await _subvaultRepository.GetManyByOrganizationIdAsync(organizationId);
            var filteredSubvaults = subvaults.Where(s => orgSubvaults.Any(os => os.Id == s.SubvaultId));

            var orgUser = new OrganizationUser
            {
                OrganizationId = organizationId,
                UserId = null,
                Email = email,
                Key = null,
                Type = type,
                Status = Enums.OrganizationUserStatusType.Invited,
                CreationDate = DateTime.UtcNow,
                RevisionDate = DateTime.UtcNow
            };

            await _organizationUserRepository.CreateAsync(orgUser);
            await SaveUserSubvaultsAsync(orgUser, filteredSubvaults, true);
            await SendInviteAsync(orgUser);

            return orgUser;
        }

        public async Task ResendInviteAsync(Guid organizationId, Guid invitingUserId, Guid organizationUserId)
        {
            if(!(await OrganizationUserHasAdminRightsAsync(organizationId, invitingUserId)))
            {
                throw new BadRequestException("Cannot invite users.");
            }

            var orgUser = await _organizationUserRepository.GetByIdAsync(organizationUserId);
            if(orgUser == null || orgUser.OrganizationId != organizationId ||
                orgUser.Status != Enums.OrganizationUserStatusType.Invited)
            {
                throw new BadRequestException("User invalid.");
            }

            await SendInviteAsync(orgUser);
        }

        private async Task SendInviteAsync(OrganizationUser orgUser)
        {
            var org = await _organizationRepository.GetByIdAsync(orgUser.OrganizationId);
            var nowMillis = CoreHelpers.ToEpocMilliseconds(DateTime.UtcNow);
            var token = _dataProtector.Protect(
                $"OrganizationUserInvite {orgUser.Id} {orgUser.Email} {nowMillis}");
            await _mailService.SendOrganizationInviteEmailAsync(org.Name, orgUser, token);
        }

        public async Task<OrganizationUser> AcceptUserAsync(Guid organizationUserId, User user, string token)
        {
            var orgUser = await _organizationUserRepository.GetByIdAsync(organizationUserId);
            if(orgUser == null || orgUser.Email != user.Email)
            {
                throw new BadRequestException("User invalid.");
            }

            if(orgUser.Status != Enums.OrganizationUserStatusType.Invited)
            {
                throw new BadRequestException("Already accepted.");
            }

            var tokenValidationFailed = true;
            try
            {
                var unprotectedData = _dataProtector.Unprotect(token);
                var dataParts = unprotectedData.Split(' ');
                if(dataParts.Length == 4 && dataParts[0] == "OrganizationUserInvite" &&
                    new Guid(dataParts[1]) == orgUser.Id && dataParts[2] == user.Email)
                {
                    var creationTime = CoreHelpers.FromEpocMilliseconds(Convert.ToInt64(dataParts[3]));
                    tokenValidationFailed = creationTime.AddDays(5) < DateTime.UtcNow;
                }
            }
            catch
            {
                tokenValidationFailed = true;
            }

            if(tokenValidationFailed)
            {
                throw new BadRequestException("Invalid token.");
            }

            orgUser.Status = Enums.OrganizationUserStatusType.Accepted;
            orgUser.UserId = user.Id;
            orgUser.Email = null;
            await _organizationUserRepository.ReplaceAsync(orgUser);

            // TODO: send email

            return orgUser;
        }

        public async Task<OrganizationUser> ConfirmUserAsync(Guid organizationId, Guid organizationUserId, string key,
            Guid confirmingUserId)
        {
            if(!(await OrganizationUserHasAdminRightsAsync(organizationId, confirmingUserId)))
            {
                throw new BadRequestException("Cannot confirm users.");
            }

            var orgUser = await _organizationUserRepository.GetByIdAsync(organizationUserId);
            if(orgUser == null || orgUser.Status != Enums.OrganizationUserStatusType.Accepted ||
                orgUser.OrganizationId != organizationId)
            {
                throw new BadRequestException("User not valid.");
            }

            orgUser.Status = Enums.OrganizationUserStatusType.Confirmed;
            orgUser.Key = key;
            orgUser.Email = null;
            await _organizationUserRepository.ReplaceAsync(orgUser);

            // TODO: send email

            return orgUser;
        }

        public async Task SaveUserAsync(OrganizationUser user, Guid savingUserId, IEnumerable<SubvaultUser> subvaults)
        {
            if(user.Id.Equals(default(Guid)))
            {
                throw new BadRequestException("Invite the user first.");
            }

            if(!(await OrganizationUserHasAdminRightsAsync(user.OrganizationId, savingUserId)))
            {
                throw new BadRequestException("Cannot update users.");
            }

            var confirmedOwners = (await GetConfirmedOwnersAsync(user.OrganizationId)).ToList();
            if(user.Type != Enums.OrganizationUserType.Owner && confirmedOwners.Count == 1 && confirmedOwners[0].Id == user.Id)
            {
                throw new BadRequestException("Organization must have at least one confirmed owner.");
            }

            var orgSubvaults = await _subvaultRepository.GetManyByOrganizationIdAsync(user.OrganizationId);
            var filteredSubvaults = subvaults.Where(s => orgSubvaults.Any(os => os.Id == s.SubvaultId));

            await _organizationUserRepository.ReplaceAsync(user);
            await SaveUserSubvaultsAsync(user, filteredSubvaults, false);
        }

        public async Task DeleteUserAsync(Guid organizationId, Guid organizationUserId, Guid deletingUserId)
        {
            if(!(await OrganizationUserHasAdminRightsAsync(organizationId, deletingUserId)))
            {
                throw new BadRequestException("Cannot delete users.");
            }

            var orgUser = await _organizationUserRepository.GetByIdAsync(organizationUserId);
            if(orgUser == null || orgUser.OrganizationId != organizationId)
            {
                throw new BadRequestException("User not valid.");
            }

            var confirmedOwners = (await GetConfirmedOwnersAsync(organizationId)).ToList();
            if(confirmedOwners.Count == 1 && confirmedOwners[0].Id == organizationUserId)
            {
                throw new BadRequestException("Organization must have at least one confirmed owner.");
            }

            await _organizationUserRepository.DeleteAsync(orgUser);
        }

        private async Task<IEnumerable<OrganizationUser>> GetConfirmedOwnersAsync(Guid organizationId)
        {
            var owners = await _organizationUserRepository.GetManyByOrganizationAsync(organizationId,
                Enums.OrganizationUserType.Owner);
            return owners.Where(o => o.Status == Enums.OrganizationUserStatusType.Confirmed);
        }

        private async Task<bool> OrganizationUserHasAdminRightsAsync(Guid organizationId, Guid userId)
        {
            var orgUser = await _organizationUserRepository.GetByOrganizationAsync(organizationId, userId);
            if(orgUser == null)
            {
                return false;
            }

            return orgUser.Status == Enums.OrganizationUserStatusType.Confirmed &&
                orgUser.Type != Enums.OrganizationUserType.User;
        }

        private async Task SaveUserSubvaultsAsync(OrganizationUser user, IEnumerable<SubvaultUser> subvaults, bool newUser)
        {
            if(subvaults == null)
            {
                subvaults = new List<SubvaultUser>();
            }

            var orgSubvaults = await _subvaultRepository.GetManyByOrganizationIdAsync(user.OrganizationId);
            var currentUserSubvaults = newUser ? null : await _subvaultUserRepository.GetManyByOrganizationUserIdAsync(user.Id);

            // Let's make sure all these belong to this user and organization.
            var filteredSubvaults = subvaults.Where(s => orgSubvaults.Any(os => os.Id == s.SubvaultId));
            foreach(var subvault in filteredSubvaults)
            {
                var existingSubvaultUser = currentUserSubvaults?.FirstOrDefault(cs => cs.SubvaultId == subvault.SubvaultId);
                if(existingSubvaultUser != null)
                {
                    subvault.Id = existingSubvaultUser.Id;
                    subvault.CreationDate = existingSubvaultUser.CreationDate;
                }

                subvault.OrganizationUserId = user.Id;
                await _subvaultUserRepository.UpsertAsync(subvault);
            }

            if(!newUser)
            {
                var subvaultsToDelete = currentUserSubvaults.Where(cs =>
                    !filteredSubvaults.Any(s => s.SubvaultId == cs.SubvaultId));
                foreach(var subvault in subvaultsToDelete)
                {
                    await _subvaultUserRepository.DeleteAsync(subvault);
                }
            }
        }
    }
}
