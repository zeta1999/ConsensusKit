﻿using System;
using System.Collections.Generic;
using System.Linq;
using Tcgv.ConsensusKit.Actors;
using Tcgv.ConsensusKit.Algorithms.Paxos.Data;
using Tcgv.ConsensusKit.Control;
using Tcgv.ConsensusKit.Exchange;
using Tcgv.ConsensusKit.Utility;

namespace Tcgv.ConsensusKit.Algorithms.Paxos
{
    public class PxProcess : Process
    {
        public PxProcess(Archiver archiver, Proposer proposer)
            : base(archiver, proposer)
        {
            proposalNumber = -1;
            minNumber = 0;
        }

        protected override void Propose(Instance r)
        {
            proposalNumber = minNumber + 1;
            Broadcast(r, MessageType.Propose, proposalNumber);
        }

        public override void Bind(Instance r)
        {
            if (r.Proposers.Contains(this))
                BindAsProposer(r);
            else
                BindAsAccepter(r);
        }

        private void BindAsProposer(Instance r)
        {
            WaitQuorum(r, MessageType.Ack, msgs =>
            {
                var v = PickHighestNumberedValue(msgs)?.Value ?? Proposer.GetProposal();

                if (Archiver.CanCommit(v))
                {
                    var x = new NumberedValue(v, proposalNumber);
                    Broadcast(r, MessageType.Select, x);
                }
            });

            WaitMessage(r, MessageType.Nack, msg =>
            {
                if (msg.Value != null)
                {
                    var n = (long)msg.Value;
                    if (n > minNumber)
                    {
                        minNumber = Math.Max(n, minNumber);
                        if (RandomExtensions.Tryout(0.5))
                            Propose(r);
                    }
                }
            });

            WaitQuorum(r, MessageType.Accept, msgs =>
            {
                var m = msgs.Select(m => m.Value).Distinct();

                if (m.Count() == 1)
                {
                    var x = m.Single() as NumberedValue;
                    Terminate(r, x);
                    Broadcast(r, MessageType.Decide, x);
                }
            });
        }

        private void BindAsAccepter(Instance r)
        {
            WaitMessage(r, MessageType.Propose, msg =>
            {
                var n = (long)msg.Value;

                if (n > minNumber)
                {
                    minNumber = n;
                    SendTo(msg.Source, r, MessageType.Ack, accepted);
                }
                else
                {
                    SendTo(msg.Source, r, MessageType.Nack, minNumber);
                }
            });

            WaitMessage(r, MessageType.Select, msg =>
            {
                var x = msg.Value as NumberedValue;

                if (x.Number >= minNumber && Archiver.CanCommit(x.Value))
                {
                    accepted = x;
                    SendTo(msg.Source, r, MessageType.Accept, x);
                }
            });

            WaitMessage(r, MessageType.Decide, msg =>
            {
                var x = msg.Value as NumberedValue;
                Terminate(r, x);
            });
        }

        private void Terminate(Instance r, NumberedValue x)
        {
            if (x.Value == accepted?.Value)
                accepted = null;
            Terminate(r, x.Value);
        }

        private NumberedValue PickHighestNumberedValue(IEnumerable<Message> msgs)
        {
            return (from m in msgs
                    where m.Value != null
                    select m.Value as NumberedValue)
                   .OrderByDescending(v => v.Number)
                   .FirstOrDefault();
        }

        private long proposalNumber;
        private long minNumber;
        private NumberedValue accepted;
    }
}