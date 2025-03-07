﻿/*
Technitium DNS Server
Copyright (C) 2023  Shreyas Zare (shreyas@technitium.com)

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.

*/

using DnsServerCore.Dns.ResourceRecords;
using DnsServerCore.Dns.Trees;
using DnsServerCore.Dns.Zones;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using TechnitiumLibrary.Net;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.EDnsOptions;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace DnsServerCore.Dns.ZoneManagers
{
    public sealed class CacheZoneManager : DnsCache
    {
        #region variables

        public const uint FAILURE_RECORD_TTL = 60u;
        public const uint NEGATIVE_RECORD_TTL = 300u;
        public const uint MINIMUM_RECORD_TTL = 10u;
        public const uint MAXIMUM_RECORD_TTL = 7 * 24 * 60 * 60;
        public const uint SERVE_STALE_TTL = 3 * 24 * 60 * 60; //3 days serve stale ttl as per https://www.rfc-editor.org/rfc/rfc8767.html suggestion

        readonly DnsServer _dnsServer;

        readonly CacheZoneTree _root = new CacheZoneTree();

        long _maximumEntries;
        long _totalEntries;

        #endregion

        #region constructor

        public CacheZoneManager(DnsServer dnsServer)
            : base(FAILURE_RECORD_TTL, NEGATIVE_RECORD_TTL, MINIMUM_RECORD_TTL, MAXIMUM_RECORD_TTL, SERVE_STALE_TTL)
        {
            _dnsServer = dnsServer;
        }

        #endregion

        #region protected

        protected override void CacheRecords(IReadOnlyList<DnsResourceRecord> resourceRecords)
        {
            List<DnsResourceRecord> dnameRecords = null;

            //read and set glue records from base class; also collect any DNAME records found
            foreach (DnsResourceRecord resourceRecord in resourceRecords)
            {
                IReadOnlyList<DnsResourceRecord> glueRecords = GetGlueRecordsFrom(resourceRecord);
                IReadOnlyList<DnsResourceRecord> rrsigRecords = GetRRSIGRecordsFrom(resourceRecord);
                IReadOnlyList<DnsResourceRecord> nsecRecords = GetNSECRecordsFrom(resourceRecord);
                NetworkAddress eDnsClientSubnet = GetEDnsClientSubnetFrom(resourceRecord);
                bool conditionalForwardingClientSubnet = GetConditionalForwardingClientSubnetFrom(resourceRecord);

                if ((glueRecords is not null) || (rrsigRecords is not null) || (nsecRecords is not null) || (eDnsClientSubnet is not null))
                {
                    CacheRecordInfo rrInfo = resourceRecord.GetCacheRecordInfo();

                    rrInfo.GlueRecords = glueRecords;
                    rrInfo.RRSIGRecords = rrsigRecords;
                    rrInfo.NSECRecords = nsecRecords;
                    rrInfo.EDnsClientSubnet = eDnsClientSubnet;
                    rrInfo.ConditionalForwardingClientSubnet = conditionalForwardingClientSubnet;

                    if (glueRecords is not null)
                    {
                        foreach (DnsResourceRecord glueRecord in glueRecords)
                        {
                            IReadOnlyList<DnsResourceRecord> glueRRSIGRecords = GetRRSIGRecordsFrom(glueRecord);
                            if (glueRRSIGRecords is not null)
                                glueRecord.GetCacheRecordInfo().RRSIGRecords = glueRRSIGRecords;
                        }
                    }

                    if (nsecRecords is not null)
                    {
                        foreach (DnsResourceRecord nsecRecord in nsecRecords)
                        {
                            IReadOnlyList<DnsResourceRecord> nsecRRSIGRecords = GetRRSIGRecordsFrom(nsecRecord);
                            if (nsecRRSIGRecords is not null)
                                nsecRecord.GetCacheRecordInfo().RRSIGRecords = nsecRRSIGRecords;
                        }
                    }
                }

                if (resourceRecord.Type == DnsResourceRecordType.DNAME)
                {
                    if (dnameRecords is null)
                        dnameRecords = new List<DnsResourceRecord>(1);

                    dnameRecords.Add(resourceRecord);
                }
            }

            if (resourceRecords.Count == 1)
            {
                DnsResourceRecord resourceRecord = resourceRecords[0];

                CacheZone zone = _root.GetOrAdd(resourceRecord.Name, delegate (string key)
                {
                    return new CacheZone(resourceRecord.Name, 1);
                });

                if (zone.SetRecords(resourceRecord.Type, resourceRecords, _dnsServer.ServeStale))
                    Interlocked.Increment(ref _totalEntries);
            }
            else
            {
                Dictionary<string, Dictionary<DnsResourceRecordType, List<DnsResourceRecord>>> groupedByDomainRecords = DnsResourceRecord.GroupRecords(resourceRecords);
                bool serveStale = _dnsServer.ServeStale;

                int addedEntries = 0;

                //add grouped records
                foreach (KeyValuePair<string, Dictionary<DnsResourceRecordType, List<DnsResourceRecord>>> groupedByTypeRecords in groupedByDomainRecords)
                {
                    if (dnameRecords is not null)
                    {
                        bool foundSynthesizedCNAME = false;

                        foreach (DnsResourceRecord dnameRecord in dnameRecords)
                        {
                            if (groupedByTypeRecords.Key.EndsWith("." + dnameRecord.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                foundSynthesizedCNAME = true;
                                break;
                            }
                        }

                        if (foundSynthesizedCNAME)
                            continue; //do not cache synthesized CNAME
                    }

                    CacheZone zone = _root.GetOrAdd(groupedByTypeRecords.Key, delegate (string key)
                    {
                        return new CacheZone(groupedByTypeRecords.Key, groupedByTypeRecords.Value.Count);
                    });

                    foreach (KeyValuePair<DnsResourceRecordType, List<DnsResourceRecord>> groupedRecords in groupedByTypeRecords.Value)
                    {
                        if (zone.SetRecords(groupedRecords.Key, groupedRecords.Value, serveStale))
                            addedEntries++;
                    }
                }

                if (addedEntries > 0)
                    Interlocked.Add(ref _totalEntries, addedEntries);
            }
        }

        #endregion

        #region private

        private static IReadOnlyList<DnsResourceRecord> AddDSRecordsTo(CacheZone delegation, bool serveStale, IReadOnlyList<DnsResourceRecord> nsRecords, NetworkAddress eDnsClientSubnet, bool conditionalForwardingClientSubnet)
        {
            IReadOnlyList<DnsResourceRecord> records = delegation.QueryRecords(DnsResourceRecordType.DS, serveStale, true, eDnsClientSubnet, conditionalForwardingClientSubnet);
            if ((records.Count > 0) && (records[0].Type == DnsResourceRecordType.DS))
            {
                List<DnsResourceRecord> newNSRecords = new List<DnsResourceRecord>(nsRecords.Count + records.Count);

                newNSRecords.AddRange(nsRecords);
                newNSRecords.AddRange(records);

                return newNSRecords;
            }

            //no DS records found check for NSEC records
            IReadOnlyList<DnsResourceRecord> nsecRecords = nsRecords[0].GetCacheRecordInfo().NSECRecords;
            if (nsecRecords is not null)
            {
                List<DnsResourceRecord> newNSRecords = new List<DnsResourceRecord>(nsRecords.Count + nsecRecords.Count);

                newNSRecords.AddRange(nsRecords);
                newNSRecords.AddRange(nsecRecords);

                return newNSRecords;
            }

            //found nothing; return original NS records
            return nsRecords;
        }

        private static void AddRRSIGRecords(IReadOnlyList<DnsResourceRecord> answer, out IReadOnlyList<DnsResourceRecord> newAnswer, out IReadOnlyList<DnsResourceRecord> newAuthority)
        {
            List<DnsResourceRecord> newAnswerList = new List<DnsResourceRecord>(answer.Count * 2);
            List<DnsResourceRecord> newAuthorityList = null;

            foreach (DnsResourceRecord record in answer)
            {
                newAnswerList.Add(record);

                CacheRecordInfo rrInfo = record.GetCacheRecordInfo();

                IReadOnlyList<DnsResourceRecord> rrsigRecords = rrInfo.RRSIGRecords;
                if (rrsigRecords is not null)
                {
                    newAnswerList.AddRange(rrsigRecords);

                    foreach (DnsResourceRecord rrsigRecord in rrsigRecords)
                    {
                        if (!DnsRRSIGRecordData.IsWildcard(rrsigRecord))
                            continue;

                        //add NSEC/NSEC3 for the wildcard proof
                        if (newAuthorityList is null)
                            newAuthorityList = new List<DnsResourceRecord>(2);

                        IReadOnlyList<DnsResourceRecord> nsecRecords = rrInfo.NSECRecords;
                        if (nsecRecords is not null)
                        {
                            foreach (DnsResourceRecord nsecRecord in nsecRecords)
                            {
                                newAuthorityList.Add(nsecRecord);

                                IReadOnlyList<DnsResourceRecord> nsecRRSIGRecords = nsecRecord.GetCacheRecordInfo().RRSIGRecords;
                                if (nsecRRSIGRecords is not null)
                                    newAuthorityList.AddRange(nsecRRSIGRecords);
                            }
                        }
                    }
                }
            }

            newAnswer = newAnswerList;
            newAuthority = newAuthorityList;
        }

        private void ResolveCNAME(DnsQuestionRecord question, DnsResourceRecord lastCNAME, bool serveStale, NetworkAddress eDnsClientSubnet, bool conditionalForwardingClientSubnet, List<DnsResourceRecord> answerRecords)
        {
            int queryCount = 0;

            do
            {
                string cnameDomain = (lastCNAME.RDATA as DnsCNAMERecordData).Domain;
                if (lastCNAME.Name.Equals(cnameDomain, StringComparison.OrdinalIgnoreCase))
                    break; //loop detected

                if (!_root.TryGet(cnameDomain, out CacheZone cacheZone))
                    break;

                IReadOnlyList<DnsResourceRecord> records = cacheZone.QueryRecords(question.Type, serveStale, true, eDnsClientSubnet, conditionalForwardingClientSubnet);
                if (records.Count < 1)
                    break;

                DnsResourceRecord lastRR = records[records.Count - 1];
                if (lastRR.Type != DnsResourceRecordType.CNAME)
                {
                    answerRecords.AddRange(records);
                    break;
                }

                foreach (DnsResourceRecord answerRecord in answerRecords)
                {
                    if (answerRecord.Type != DnsResourceRecordType.CNAME)
                        continue;

                    if (answerRecord.RDATA.Equals(lastRR.RDATA))
                        return; //loop detected
                }

                answerRecords.AddRange(records);

                lastCNAME = lastRR;
            }
            while (++queryCount < DnsServer.MAX_CNAME_HOPS);
        }

        private bool DoDNAMESubstitution(DnsQuestionRecord question, IReadOnlyList<DnsResourceRecord> answer, bool serveStale, NetworkAddress eDnsClientSubnet, bool conditionalForwardingClientSubnet, out IReadOnlyList<DnsResourceRecord> newAnswer)
        {
            DnsResourceRecord dnameRR = answer[0];

            string result = (dnameRR.RDATA as DnsDNAMERecordData).Substitute(question.Name, dnameRR.Name);

            if (DnsClient.IsDomainNameValid(result))
            {
                DnsResourceRecord cnameRR = new DnsResourceRecord(question.Name, DnsResourceRecordType.CNAME, question.Class, dnameRR.TTL, new DnsCNAMERecordData(result));

                List<DnsResourceRecord> list = new List<DnsResourceRecord>(5)
                {
                    dnameRR,
                    cnameRR
                };

                ResolveCNAME(question, cnameRR, serveStale, eDnsClientSubnet, conditionalForwardingClientSubnet, list);

                newAnswer = list;
                return true;
            }
            else
            {
                newAnswer = answer;
                return false;
            }
        }

        private IReadOnlyList<DnsResourceRecord> GetAdditionalRecords(IReadOnlyList<DnsResourceRecord> refRecords, bool serveStale, bool dnssecOk, NetworkAddress eDnsClientSubnet, bool conditionalForwardingClientSubnet)
        {
            List<DnsResourceRecord> additionalRecords = new List<DnsResourceRecord>();

            foreach (DnsResourceRecord refRecord in refRecords)
            {
                switch (refRecord.Type)
                {
                    case DnsResourceRecordType.NS:
                        if (refRecord.RDATA is DnsNSRecordData ns)
                            ResolveAdditionalRecords(refRecord, ns.NameServer, serveStale, dnssecOk, eDnsClientSubnet, conditionalForwardingClientSubnet, additionalRecords);

                        break;

                    case DnsResourceRecordType.MX:
                        if (refRecord.RDATA is DnsMXRecordData mx)
                            ResolveAdditionalRecords(refRecord, mx.Exchange, serveStale, dnssecOk, eDnsClientSubnet, conditionalForwardingClientSubnet, additionalRecords);

                        break;

                    case DnsResourceRecordType.SRV:
                        if (refRecord.RDATA is DnsSRVRecordData srv)
                            ResolveAdditionalRecords(refRecord, srv.Target, serveStale, dnssecOk, eDnsClientSubnet, conditionalForwardingClientSubnet, additionalRecords);

                        break;

                    case DnsResourceRecordType.SVCB:
                    case DnsResourceRecordType.HTTPS:
                        if (refRecord.RDATA is DnsSVCBRecordData svcb)
                        {
                            string targetName = svcb.TargetName;

                            if (svcb.SvcPriority == 0)
                            {
                                //For AliasMode SVCB RRs, a TargetName of "." indicates that the service is not available or does not exist [draft-ietf-dnsop-svcb-https-12]
                                if ((targetName.Length == 0) || targetName.Equals(refRecord.Name, StringComparison.OrdinalIgnoreCase))
                                    break;
                            }
                            else
                            {
                                //For ServiceMode SVCB RRs, if TargetName has the value ".", then the owner name of this record MUST be used as the effective TargetName [draft-ietf-dnsop-svcb-https-12]
                                if (targetName.Length == 0)
                                    targetName = refRecord.Name;
                            }

                            ResolveAdditionalRecords(refRecord, targetName, serveStale, dnssecOk, eDnsClientSubnet, conditionalForwardingClientSubnet, additionalRecords);
                        }

                        break;
                }
            }

            return additionalRecords;
        }

        private void ResolveAdditionalRecords(DnsResourceRecord refRecord, string domain, bool serveStale, bool dnssecOk, NetworkAddress eDnsClientSubnet, bool conditionalForwardingClientSubnet, List<DnsResourceRecord> additionalRecords)
        {
            IReadOnlyList<DnsResourceRecord> glueRecords = refRecord.GetCacheRecordInfo().GlueRecords;
            if (glueRecords is not null)
            {
                bool added = false;

                foreach (DnsResourceRecord glueRecord in glueRecords)
                {
                    if (!glueRecord.IsStale)
                    {
                        added = true;
                        additionalRecords.Add(glueRecord);

                        if (dnssecOk)
                        {
                            IReadOnlyList<DnsResourceRecord> rrsigRecords = glueRecord.GetCacheRecordInfo().RRSIGRecords;
                            if (rrsigRecords is not null)
                                additionalRecords.AddRange(rrsigRecords);
                        }
                    }
                }

                if (added)
                    return;
            }

            int count = 0;

            while ((count++ < DnsServer.MAX_CNAME_HOPS) && _root.TryGet(domain, out CacheZone cacheZone))
            {
                if (((refRecord.Type == DnsResourceRecordType.SVCB) || (refRecord.Type == DnsResourceRecordType.HTTPS)) && ((refRecord.RDATA as DnsSVCBRecordData).SvcPriority == 0))
                {
                    //resolve SVCB/HTTPS for Alias mode refRecord
                    IReadOnlyList<DnsResourceRecord> records = cacheZone.QueryRecords(refRecord.Type, serveStale, true, eDnsClientSubnet, conditionalForwardingClientSubnet);
                    if ((records.Count > 0) && (records[0].Type == refRecord.Type) && (records[0].RDATA is DnsSVCBRecordData svcb))
                    {
                        additionalRecords.AddRange(records);

                        string targetName = svcb.TargetName;

                        if (svcb.SvcPriority == 0)
                        {
                            //Alias mode
                            if ((targetName.Length == 0) || targetName.Equals(records[0].Name, StringComparison.OrdinalIgnoreCase))
                                break; //For AliasMode SVCB RRs, a TargetName of "." indicates that the service is not available or does not exist [draft-ietf-dnsop-svcb-https-12]

                            foreach (DnsResourceRecord additionalRecord in additionalRecords)
                            {
                                if (additionalRecord.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                                    return; //loop detected
                            }

                            //continue to resolve SVCB/HTTPS further
                            domain = targetName;
                            refRecord = records[0];
                            continue;
                        }
                        else
                        {
                            //Service mode
                            if (targetName.Length > 0)
                            {
                                //continue to resolve A/AAAA for target name
                                domain = targetName;
                                refRecord = records[0];
                                continue;
                            }

                            //resolve A/AAAA below
                        }
                    }
                }

                {
                    IReadOnlyList<DnsResourceRecord> records = cacheZone.QueryRecords(DnsResourceRecordType.A, serveStale, true, eDnsClientSubnet, conditionalForwardingClientSubnet);
                    if ((records.Count > 0) && (records[0].Type == DnsResourceRecordType.A))
                        additionalRecords.AddRange(records);
                }

                {
                    IReadOnlyList<DnsResourceRecord> records = cacheZone.QueryRecords(DnsResourceRecordType.AAAA, serveStale, true, eDnsClientSubnet, conditionalForwardingClientSubnet);
                    if ((records.Count > 0) && (records[0].Type == DnsResourceRecordType.AAAA))
                        additionalRecords.AddRange(records);
                }

                break;
            }
        }

        private int RemoveExpiredRecordsInternal(bool serveStale, long minimumEntriesToRemove)
        {
            int removedEntries = 0;

            foreach (CacheZone zone in _root)
            {
                removedEntries += zone.RemoveExpiredRecords(serveStale);

                if (zone.IsEmpty)
                    _root.TryRemove(zone.Name, out _); //remove empty zone

                if ((minimumEntriesToRemove > 0) && (removedEntries >= minimumEntriesToRemove))
                    break;
            }

            if (removedEntries > 0)
            {
                long totalEntries = Interlocked.Add(ref _totalEntries, -removedEntries);
                if (totalEntries < 0)
                    Interlocked.Add(ref _totalEntries, -totalEntries);
            }

            return removedEntries;
        }

        private int RemoveLeastUsedRecordsInternal(DateTime cutoff, long minimumEntriesToRemove)
        {
            int removedEntries = 0;

            foreach (CacheZone zone in _root)
            {
                removedEntries += zone.RemoveLeastUsedRecords(cutoff);

                if (zone.IsEmpty)
                    _root.TryRemove(zone.Name, out _); //remove empty zone

                if ((minimumEntriesToRemove > 0) && (removedEntries >= minimumEntriesToRemove))
                    break;
            }

            if (removedEntries > 0)
            {
                long totalEntries = Interlocked.Add(ref _totalEntries, -removedEntries);
                if (totalEntries < 0)
                    Interlocked.Add(ref _totalEntries, -totalEntries);
            }

            return removedEntries;
        }

        #endregion

        #region public

        public override void RemoveExpiredRecords()
        {
            bool serveStale = _dnsServer.ServeStale;

            //remove expired records/expired stale records
            RemoveExpiredRecordsInternal(serveStale, 0);

            if (_maximumEntries < 1)
                return; //cache limit feature disabled

            //find minimum entries to remove
            long minimumEntriesToRemove = _totalEntries - _maximumEntries;
            if (minimumEntriesToRemove < 1)
                return; //no need to remove

            //remove stale records if they exists
            if (serveStale)
                minimumEntriesToRemove -= RemoveExpiredRecordsInternal(false, minimumEntriesToRemove);

            if (minimumEntriesToRemove < 1)
                return; //task completed

            //remove least recently used records
            for (int seconds = 86400; seconds > 0; seconds /= 2)
            {
                DateTime cutoff = DateTime.UtcNow.AddSeconds(-seconds);

                minimumEntriesToRemove -= RemoveLeastUsedRecordsInternal(cutoff, minimumEntriesToRemove);

                if (minimumEntriesToRemove < 1)
                    break; //task completed
            }
        }

        public void DeleteEDnsClientSubnetData()
        {
            int removedEntries = 0;

            foreach (CacheZone zone in _root)
            {
                removedEntries += zone.DeleteEDnsClientSubnetData();

                if (zone.IsEmpty)
                    _root.TryRemove(zone.Name, out _); //remove empty zone
            }

            if (removedEntries > 0)
            {
                long totalEntries = Interlocked.Add(ref _totalEntries, -removedEntries);
                if (totalEntries < 0)
                    Interlocked.Add(ref _totalEntries, -totalEntries);
            }
        }

        public override void Flush()
        {
            _root.Clear();

            long totalEntries = _totalEntries;
            totalEntries = Interlocked.Add(ref _totalEntries, -totalEntries);
            if (totalEntries < 0)
                Interlocked.Add(ref _totalEntries, -totalEntries);
        }

        public bool DeleteZone(string domain)
        {
            if (_root.TryRemoveTree(domain, out _, out int removedEntries))
            {
                if (removedEntries > 0)
                {
                    long totalEntries = Interlocked.Add(ref _totalEntries, -removedEntries);
                    if (totalEntries < 0)
                        Interlocked.Add(ref _totalEntries, -totalEntries);
                }

                return true;
            }

            return false;
        }

        public void ListSubDomains(string domain, List<string> subDomains)
        {
            _root.ListSubDomains(domain, subDomains);
        }

        public void ListAllRecords(string domain, List<DnsResourceRecord> records)
        {
            if (_root.TryGet(domain, out CacheZone zone))
                zone.ListAllRecords(records);
        }

        public override DnsDatagram QueryClosestDelegation(DnsDatagram request)
        {
            string domain = request.Question[0].Name;

            NetworkAddress eDnsClientSubnet = null;
            bool conditionalForwardingClientSubnet = false;
            {
                EDnsClientSubnetOptionData requestECS = request.GetEDnsClientSubnetOption();
                if (requestECS is not null)
                {
                    eDnsClientSubnet = new NetworkAddress(requestECS.Address, requestECS.SourcePrefixLength);
                    conditionalForwardingClientSubnet = requestECS.ConditionalForwardingClientSubnet;
                }
            }

            do
            {
                _ = _root.FindZone(domain, out _, out CacheZone delegation);
                if (delegation is null)
                    return null;

                //return closest name servers in delegation
                IReadOnlyList<DnsResourceRecord> closestAuthority = delegation.QueryRecords(DnsResourceRecordType.NS, false, true, eDnsClientSubnet, conditionalForwardingClientSubnet);
                if ((closestAuthority.Count > 0) && (closestAuthority[0].Type == DnsResourceRecordType.NS) && (closestAuthority[0].Name.Length > 0)) //dont trust root name servers from cache!
                {
                    if (request.DnssecOk)
                    {
                        if (closestAuthority[0].DnssecStatus != DnssecStatus.Disabled) //dont return records with disabled status
                        {
                            closestAuthority = AddDSRecordsTo(delegation, false, closestAuthority, eDnsClientSubnet, conditionalForwardingClientSubnet);

                            IReadOnlyList<DnsResourceRecord> additional = GetAdditionalRecords(closestAuthority, false, request.DnssecOk, eDnsClientSubnet, conditionalForwardingClientSubnet);

                            return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, false, false, request.RecursionDesired, true, false, false, DnsResponseCode.NoError, request.Question, null, closestAuthority, additional);
                        }
                    }
                    else
                    {
                        IReadOnlyList<DnsResourceRecord> additional = GetAdditionalRecords(closestAuthority, false, request.DnssecOk, eDnsClientSubnet, conditionalForwardingClientSubnet);

                        return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, false, false, request.RecursionDesired, true, false, false, DnsResponseCode.NoError, request.Question, null, closestAuthority, additional);
                    }
                }

                domain = AuthZoneManager.GetParentZone(delegation.Name);
            }
            while (domain is not null);

            //no cached delegation found
            return null;
        }

        public override DnsDatagram Query(DnsDatagram request, bool serveStaleAndResetExpiry = false, bool findClosestNameServers = false)
        {
            DnsQuestionRecord question = request.Question[0];

            NetworkAddress eDnsClientSubnet = null;
            bool conditionalForwardingClientSubnet = false;
            {
                EDnsClientSubnetOptionData requestECS = request.GetEDnsClientSubnetOption();
                if (requestECS is not null)
                {
                    eDnsClientSubnet = new NetworkAddress(requestECS.Address, requestECS.SourcePrefixLength);
                    conditionalForwardingClientSubnet = requestECS.ConditionalForwardingClientSubnet;
                }
            }

            CacheZone zone;
            CacheZone closest = null;
            CacheZone delegation = null;

            if (findClosestNameServers)
            {
                zone = _root.FindZone(question.Name, out closest, out delegation);
            }
            else
            {
                if (!_root.TryGet(question.Name, out zone))
                    _ = _root.FindZone(question.Name, out closest, out _); //zone not found; attempt to find closest
            }

            if (zone is not null)
            {
                //zone found
                IReadOnlyList<DnsResourceRecord> answer = zone.QueryRecords(question.Type, serveStaleAndResetExpiry, false, eDnsClientSubnet, conditionalForwardingClientSubnet);
                if (answer.Count > 0)
                {
                    //answer found in cache
                    DnsResourceRecord firstRR = answer[0];

                    if (firstRR.RDATA is DnsSpecialCacheRecordData dnsSpecialCacheRecord)
                    {
                        if (request.DnssecOk)
                        {
                            foreach (DnsResourceRecord originalAuthority in dnsSpecialCacheRecord.OriginalAuthority)
                            {
                                if (originalAuthority.DnssecStatus == DnssecStatus.Disabled)
                                    goto beforeFindClosestNameServers; //dont return answer with disabled status
                            }
                        }

                        if (serveStaleAndResetExpiry)
                        {
                            if (firstRR.IsStale)
                                firstRR.ResetExpiry(30); //reset expiry by 30 seconds so that resolver tries again only after 30 seconds as per draft-ietf-dnsop-serve-stale-04

                            if (dnsSpecialCacheRecord.Authority is not null)
                            {
                                foreach (DnsResourceRecord record in dnsSpecialCacheRecord.Authority)
                                {
                                    if (record.IsStale)
                                        record.ResetExpiry(30); //reset expiry by 30 seconds so that resolver tries again only after 30 seconds as per draft-ietf-dnsop-serve-stale-04
                                }
                            }
                        }

                        IReadOnlyList<EDnsOption> specialOptions;

                        if (firstRR.WasExpiryReset)
                        {
                            List<EDnsOption> newOptions = new List<EDnsOption>(dnsSpecialCacheRecord.EDnsOptions.Count + 1);

                            newOptions.AddRange(dnsSpecialCacheRecord.EDnsOptions);

                            if (dnsSpecialCacheRecord.RCODE == DnsResponseCode.NxDomain)
                                newOptions.Add(new EDnsOption(EDnsOptionCode.EXTENDED_DNS_ERROR, new EDnsExtendedDnsErrorOptionData(EDnsExtendedDnsErrorCode.StaleNxDomainAnswer, null)));
                            else
                                newOptions.Add(new EDnsOption(EDnsOptionCode.EXTENDED_DNS_ERROR, new EDnsExtendedDnsErrorOptionData(EDnsExtendedDnsErrorCode.StaleAnswer, null)));

                            specialOptions = newOptions;
                        }
                        else
                        {
                            specialOptions = dnsSpecialCacheRecord.EDnsOptions;
                        }

                        if (eDnsClientSubnet is not null)
                        {
                            EDnsClientSubnetOptionData requestECS = request.GetEDnsClientSubnetOption(true);
                            if (requestECS is not null)
                            {
                                NetworkAddress recordECS = firstRR.GetCacheRecordInfo().EDnsClientSubnet;
                                if (recordECS is not null)
                                {
                                    EDnsOption[] ecsOption = EDnsClientSubnetOptionData.GetEDnsClientSubnetOption(requestECS.SourcePrefixLength, recordECS.PrefixLength, requestECS.Address);

                                    if ((specialOptions is null) || (specialOptions.Count == 0))
                                    {
                                        specialOptions = ecsOption;
                                    }
                                    else
                                    {
                                        List<EDnsOption> newOptions = new List<EDnsOption>(specialOptions.Count + 1);

                                        newOptions.AddRange(specialOptions);
                                        newOptions.Add(ecsOption[0]);

                                        specialOptions = newOptions;
                                    }
                                }
                            }
                        }

                        if (request.DnssecOk)
                        {
                            bool authenticData;

                            switch (dnsSpecialCacheRecord.Type)
                            {
                                case DnsSpecialCacheRecordType.NegativeCache:
                                    authenticData = true;
                                    break;

                                default:
                                    authenticData = false;
                                    break;
                            }

                            if (request.CheckingDisabled)
                                return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, false, false, request.RecursionDesired, true, authenticData, request.CheckingDisabled, dnsSpecialCacheRecord.OriginalRCODE, request.Question, dnsSpecialCacheRecord.OriginalAnswer, dnsSpecialCacheRecord.OriginalAuthority, dnsSpecialCacheRecord.Additional, _dnsServer.UdpPayloadSize, EDnsHeaderFlags.DNSSEC_OK, specialOptions);
                            else
                                return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, false, false, request.RecursionDesired, true, authenticData, request.CheckingDisabled, dnsSpecialCacheRecord.RCODE, request.Question, null, dnsSpecialCacheRecord.Authority, null, _dnsServer.UdpPayloadSize, EDnsHeaderFlags.DNSSEC_OK, specialOptions);
                        }
                        else
                        {
                            return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, false, false, request.RecursionDesired, true, false, false, dnsSpecialCacheRecord.RCODE, request.Question, null, dnsSpecialCacheRecord.NoDnssecAuthority, null, request.EDNS is null ? ushort.MinValue : _dnsServer.UdpPayloadSize, EDnsHeaderFlags.None, specialOptions);
                        }
                    }

                    DnsResourceRecord lastRR = answer[answer.Count - 1];
                    if ((lastRR.Type != question.Type) && (lastRR.Type == DnsResourceRecordType.CNAME) && (question.Type != DnsResourceRecordType.ANY))
                    {
                        List<DnsResourceRecord> newAnswers = new List<DnsResourceRecord>(answer.Count + 3);
                        newAnswers.AddRange(answer);

                        ResolveCNAME(question, lastRR, serveStaleAndResetExpiry, eDnsClientSubnet, conditionalForwardingClientSubnet, newAnswers);

                        answer = newAnswers;
                    }

                    IReadOnlyList<DnsResourceRecord> authority = null;
                    EDnsHeaderFlags ednsFlags = EDnsHeaderFlags.None;

                    if (request.DnssecOk)
                    {
                        //DNSSEC enabled
                        foreach (DnsResourceRecord record in answer)
                        {
                            if (record.DnssecStatus == DnssecStatus.Disabled)
                                goto beforeFindClosestNameServers; //dont return answer when status is disabled
                        }

                        //add RRSIG records
                        AddRRSIGRecords(answer, out answer, out authority);

                        ednsFlags = EDnsHeaderFlags.DNSSEC_OK;
                    }

                    IReadOnlyList<DnsResourceRecord> additional = null;

                    switch (question.Type)
                    {
                        case DnsResourceRecordType.NS:
                        case DnsResourceRecordType.MX:
                        case DnsResourceRecordType.SRV:
                        case DnsResourceRecordType.SVCB:
                        case DnsResourceRecordType.HTTPS:
                            additional = GetAdditionalRecords(answer, serveStaleAndResetExpiry, request.DnssecOk, eDnsClientSubnet, conditionalForwardingClientSubnet);
                            break;
                    }

                    IReadOnlyList<EDnsOption> options = null;

                    if (serveStaleAndResetExpiry)
                    {
                        foreach (DnsResourceRecord record in answer)
                        {
                            if (record.IsStale)
                                record.ResetExpiry(30); //reset expiry by 30 seconds so that resolver tries again only after 30 seconds as per draft-ietf-dnsop-serve-stale-04
                        }

                        if (additional is not null)
                        {
                            foreach (DnsResourceRecord record in additional)
                            {
                                if (record.IsStale)
                                    record.ResetExpiry(30); //reset expiry by 30 seconds so that resolver tries again only after 30 seconds as per draft-ietf-dnsop-serve-stale-04
                            }
                        }

                        options = new EDnsOption[] { new EDnsOption(EDnsOptionCode.EXTENDED_DNS_ERROR, new EDnsExtendedDnsErrorOptionData(EDnsExtendedDnsErrorCode.StaleAnswer, null)) };
                    }
                    else
                    {
                        foreach (DnsResourceRecord record in answer)
                        {
                            if (record.WasExpiryReset)
                            {
                                options = new EDnsOption[] { new EDnsOption(EDnsOptionCode.EXTENDED_DNS_ERROR, new EDnsExtendedDnsErrorOptionData(EDnsExtendedDnsErrorCode.StaleAnswer, null)) };
                                break;
                            }
                        }
                    }

                    if (eDnsClientSubnet is not null)
                    {
                        EDnsClientSubnetOptionData requestECS = request.GetEDnsClientSubnetOption(true);
                        if (requestECS is not null)
                        {
                            NetworkAddress suitableECS = null;

                            foreach (DnsResourceRecord record in answer)
                            {
                                NetworkAddress recordECS = record.GetCacheRecordInfo().EDnsClientSubnet;
                                if (recordECS is not null)
                                {
                                    if ((suitableECS is null) || (recordECS.PrefixLength > suitableECS.PrefixLength))
                                        suitableECS = recordECS;
                                }
                            }

                            if (suitableECS is not null)
                            {
                                EDnsOption[] ecsOption = EDnsClientSubnetOptionData.GetEDnsClientSubnetOption(requestECS.SourcePrefixLength, suitableECS.PrefixLength, requestECS.Address);

                                if (options is null)
                                {
                                    options = ecsOption;
                                }
                                else
                                {
                                    List<EDnsOption> newOptions = new List<EDnsOption>(options.Count + 1);

                                    newOptions.AddRange(options);
                                    newOptions.Add(ecsOption[0]);

                                    options = newOptions;
                                }
                            }
                        }
                    }

                    return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, false, false, request.RecursionDesired, true, answer[0].DnssecStatus == DnssecStatus.Secure, request.CheckingDisabled, DnsResponseCode.NoError, request.Question, answer, authority, additional, request.EDNS is null ? ushort.MinValue : _dnsServer.UdpPayloadSize, ednsFlags, options);
                }
            }
            else
            {
                //zone not found
                //check for DNAME in closest zone
                if (closest is not null)
                {
                    IReadOnlyList<DnsResourceRecord> answer = closest.QueryRecords(DnsResourceRecordType.DNAME, serveStaleAndResetExpiry, true, eDnsClientSubnet, conditionalForwardingClientSubnet);
                    if ((answer.Count > 0) && (answer[0].Type == DnsResourceRecordType.DNAME))
                    {
                        DnsResponseCode rCode;

                        if (DoDNAMESubstitution(question, answer, serveStaleAndResetExpiry, eDnsClientSubnet, conditionalForwardingClientSubnet, out answer))
                            rCode = DnsResponseCode.NoError;
                        else
                            rCode = DnsResponseCode.YXDomain;

                        IReadOnlyList<DnsResourceRecord> authority = null;
                        EDnsHeaderFlags ednsFlags = EDnsHeaderFlags.None;

                        if (request.DnssecOk)
                        {
                            //DNSSEC enabled
                            foreach (DnsResourceRecord record in answer)
                            {
                                if (record.DnssecStatus == DnssecStatus.Disabled)
                                    goto beforeFindClosestNameServers; //dont return answer when status is disabled
                            }

                            //add RRSIG records
                            AddRRSIGRecords(answer, out answer, out authority);

                            ednsFlags = EDnsHeaderFlags.DNSSEC_OK;
                        }

                        EDnsOption[] options = null;

                        if (serveStaleAndResetExpiry)
                        {
                            foreach (DnsResourceRecord record in answer)
                            {
                                if (record.IsStale)
                                    record.ResetExpiry(30); //reset expiry by 30 seconds so that resolver tries again only after 30 seconds as per draft-ietf-dnsop-serve-stale-04
                            }

                            options = new EDnsOption[] { new EDnsOption(EDnsOptionCode.EXTENDED_DNS_ERROR, new EDnsExtendedDnsErrorOptionData(EDnsExtendedDnsErrorCode.StaleAnswer, null)) };
                        }
                        else
                        {
                            foreach (DnsResourceRecord record in answer)
                            {
                                if (record.WasExpiryReset)
                                {
                                    options = new EDnsOption[] { new EDnsOption(EDnsOptionCode.EXTENDED_DNS_ERROR, new EDnsExtendedDnsErrorOptionData(EDnsExtendedDnsErrorCode.StaleAnswer, null)) };
                                    break;
                                }
                            }
                        }

                        return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, false, false, request.RecursionDesired, true, answer[0].DnssecStatus == DnssecStatus.Secure, request.CheckingDisabled, rCode, request.Question, answer, authority, null, request.EDNS is null ? ushort.MinValue : _dnsServer.UdpPayloadSize, ednsFlags, options);
                    }
                }
            }

            //no answer in cache
            beforeFindClosestNameServers:

            //check for closest delegation if any
            if (findClosestNameServers && (delegation is not null))
            {
                //return closest name servers in delegation
                if (question.Type == DnsResourceRecordType.DS)
                {
                    //find parent delegation
                    string domain = AuthZoneManager.GetParentZone(question.Name);
                    if (domain is null)
                        return null; //dont find NS for root

                    _ = _root.FindZone(domain, out _, out delegation);
                    if (delegation is null)
                        return null; //no cached delegation found
                }

                while (true)
                {
                    IReadOnlyList<DnsResourceRecord> closestAuthority = delegation.QueryRecords(DnsResourceRecordType.NS, serveStaleAndResetExpiry, true, eDnsClientSubnet, conditionalForwardingClientSubnet);
                    if ((closestAuthority.Count > 0) && (closestAuthority[0].Type == DnsResourceRecordType.NS) && (closestAuthority[0].Name.Length > 0)) //dont trust root name servers from cache!
                    {
                        if (request.DnssecOk)
                        {
                            if (closestAuthority[0].DnssecStatus != DnssecStatus.Disabled) //dont return records with disabled status
                            {
                                closestAuthority = AddDSRecordsTo(delegation, serveStaleAndResetExpiry, closestAuthority, eDnsClientSubnet, conditionalForwardingClientSubnet);

                                IReadOnlyList<DnsResourceRecord> additional = GetAdditionalRecords(closestAuthority, serveStaleAndResetExpiry, request.DnssecOk, eDnsClientSubnet, conditionalForwardingClientSubnet);

                                return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, false, false, request.RecursionDesired, true, closestAuthority[0].DnssecStatus == DnssecStatus.Secure, request.CheckingDisabled, DnsResponseCode.NoError, request.Question, null, closestAuthority, additional);
                            }
                        }
                        else
                        {
                            IReadOnlyList<DnsResourceRecord> additional = GetAdditionalRecords(closestAuthority, serveStaleAndResetExpiry, request.DnssecOk, eDnsClientSubnet, conditionalForwardingClientSubnet);

                            return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, false, false, request.RecursionDesired, true, closestAuthority[0].DnssecStatus == DnssecStatus.Secure, request.CheckingDisabled, DnsResponseCode.NoError, request.Question, null, closestAuthority, additional);
                        }
                    }

                    string domain = AuthZoneManager.GetParentZone(delegation.Name);
                    if (domain is null)
                        return null; //dont find NS for root

                    _ = _root.FindZone(domain, out _, out delegation);
                    if (delegation is null)
                        return null; //no cached delegation found
                }
            }

            //no cached delegation found
            return null;
        }

        public void LoadCacheZoneFile()
        {
            string cacheZoneFile = Path.Combine(_dnsServer.ConfigFolder, "cache.bin");

            if (!File.Exists(cacheZoneFile))
                return;

            _dnsServer.LogManager?.Write("Loading DNS Cache from disk...");

            using (FileStream fS = new FileStream(cacheZoneFile, FileMode.Open, FileAccess.Read))
            {
                BinaryReader bR = new BinaryReader(fS);

                if (Encoding.ASCII.GetString(bR.ReadBytes(2)) != "CZ")
                    throw new InvalidDataException("CacheZoneManager format is invalid.");

                int version = bR.ReadByte();
                switch (version)
                {
                    case 1:
                        int addedEntries = 0;

                        try
                        {
                            bool serveStale = _dnsServer.ServeStale;

                            while (bR.BaseStream.Position < bR.BaseStream.Length)
                            {
                                CacheZone zone = CacheZone.ReadFrom(bR, serveStale);
                                if (!zone.IsEmpty)
                                {
                                    if (_root.TryAdd(zone.Name, zone))
                                        addedEntries += zone.TotalEntries;
                                }
                            }
                        }
                        finally
                        {
                            if (addedEntries > 0)
                                Interlocked.Add(ref _totalEntries, addedEntries);
                        }
                        break;

                    default:
                        throw new InvalidDataException("CacheZoneManager format version not supported: " + version);
                }
            }

            _dnsServer.LogManager?.Write("DNS Cache was loaded from disk successfully.");
        }

        public void SaveCacheZoneFile()
        {
            _dnsServer.LogManager?.Write("Saving DNS Cache to disk...");

            string cacheZoneFile = Path.Combine(_dnsServer.ConfigFolder, "cache.bin");

            using (FileStream fS = new FileStream(cacheZoneFile, FileMode.Create, FileAccess.Write))
            {
                BinaryWriter bW = new BinaryWriter(fS);

                bW.Write(Encoding.ASCII.GetBytes("CZ")); //format
                bW.Write((byte)1); //version

                foreach (CacheZone zone in _root)
                    zone.WriteTo(bW);
            }

            _dnsServer.LogManager?.Write("DNS Cache was saved to disk successfully.");
        }

        public void DeleteCacheZoneFile()
        {
            string cacheZoneFile = Path.Combine(_dnsServer.ConfigFolder, "cache.bin");

            if (File.Exists(cacheZoneFile))
                File.Delete(cacheZoneFile);
        }

        #endregion

        #region properties

        public long MaximumEntries
        {
            get { return _maximumEntries; }
            set
            {
                if (value < 0)
                    throw new ArgumentOutOfRangeException(nameof(MaximumEntries), "Invalid cache maximum entries value. Valid range is 0 and above.");

                _maximumEntries = value;
            }
        }

        public long TotalEntries
        { get { return _totalEntries; } }

        #endregion
    }
}
