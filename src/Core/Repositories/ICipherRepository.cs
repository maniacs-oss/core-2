﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Bit.Core.Models.Table;
using Core.Models.Data;

namespace Bit.Core.Repositories
{
    public interface ICipherRepository : IRepository<Cipher, Guid>
    {
        Task<CipherDetails> GetByIdAsync(Guid id, Guid userId);
        Task<CipherFullDetails> GetFullDetailsByIdAsync(Guid id, Guid userId);
        Task<ICollection<CipherDetails>> GetManyByUserIdAsync(Guid userId);
        Task<ICollection<CipherDetails>> GetManyByUserIdHasSubvaultsAsync(Guid userId);
        Task<ICollection<CipherDetails>> GetManyByTypeAndUserIdAsync(Enums.CipherType type, Guid userId);
        Task<Tuple<ICollection<CipherDetails>, ICollection<Guid>>> GetManySinceRevisionDateAndUserIdWithDeleteHistoryAsync(
            DateTime sinceRevisionDate, Guid userId);
        Task CreateAsync(CipherDetails cipher);
        Task ReplaceAsync(CipherDetails cipher);
        Task UpsertAsync(CipherDetails cipher);
        Task ReplaceAsync(Cipher obj, IEnumerable<Guid> subvaultIds);
        Task UpdatePartialAsync(Guid id, Guid userId, Guid? folderId, bool favorite);
        Task UpdateUserEmailPasswordAndCiphersAsync(User user, IEnumerable<Cipher> ciphers);
        Task CreateAsync(IEnumerable<Cipher> ciphers);
    }
}
