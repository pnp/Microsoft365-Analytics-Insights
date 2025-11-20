using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.CostEstimate;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace Tests.UnitTests
{
    /// <summary>
    /// Unit tests for CopilotCreditEstimation.Analyze() method covering all billing scenarios
    /// </summary>
    [TestClass]
    public class CopilotCreditEstimationTests
    {
        #region Null & Empty Input Tests

        [TestMethod]
        public void CopilotCreditEstimation_Analyze_WithNullJson_ReturnsEmptyReport()
        {
            // Act
            var result = CopilotCreditEstimation.Analyze((string)null);
            
            // Assert
            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.TotalCredits);
            Assert.IsNotNull(result.ResourceTypeBreakdown);
            Assert.IsNotNull(result.CreditBreakdown);
            Assert.AreEqual(0, result.ResourceTypeBreakdown.Count);
            Assert.AreEqual(0, result.CreditBreakdown.Count);
        }

        [TestMethod]
        public void CopilotCreditEstimation_Analyze_WithEmptyJson_ReturnsEmptyReport()
        {
            // Act
            var result = CopilotCreditEstimation.Analyze("");
            
            // Assert
            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.TotalCredits);
        }

        [TestMethod]
        public void CopilotCreditEstimation_Analyze_WithNullAuditEvent_ReturnsEmptyReport()
        {
            // Act
            var result = CopilotCreditEstimation.Analyze((CopilotAuditEvent)null);
            
            // Assert
            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.TotalCredits);
            Assert.IsNotNull(result.ResourceTypeBreakdown);
            Assert.IsNotNull(result.CreditBreakdown);
        }

        [TestMethod]
        public void CopilotCreditEstimation_Analyze_WithEmptyAuditEvent_ReturnsZeroCredits()
        {
            // Arrange
            var auditEvent = new CopilotAuditEvent();
            
            // Act
            var result = CopilotCreditEstimation.Analyze(auditEvent);
            
            // Assert
            Assert.AreEqual(0, result.TotalCredits);
            Assert.AreEqual(0, result.ClassicAnswers);
            Assert.AreEqual(0, result.GenerativeAnswers);
            Assert.AreEqual(0, result.TenantGraphGroundedAnswers);
            Assert.AreEqual(0, result.AgentActionCount);
        }

        #endregion

        #region Message Type Billing Tests

        [TestMethod]
        public void CopilotCreditEstimation_Analyze_WithClassicAnswer_Returns1Credit()
        {
            // Arrange
            var auditEvent = new CopilotAuditEvent
            {
                Messages = new List<Message>
                {
                    new Message { IsPrompt = false, Type = "Classic" }
                }
            };
            
            // Act
            var result = CopilotCreditEstimation.Analyze(auditEvent);
            
            // Assert
            Assert.AreEqual(1, result.ClassicAnswers);
            Assert.AreEqual(1, result.TotalCredits);
            Assert.IsTrue(result.CreditBreakdown.ContainsKey("Classic Answers"));
            Assert.AreEqual(1, result.CreditBreakdown["Classic Answers"]);
        }

        [TestMethod]
        public void CopilotCreditEstimation_Analyze_WithMultipleClassicAnswers_CalculatesCorrectCredits()
        {
            // Arrange
            var auditEvent = new CopilotAuditEvent
            {
                Messages = new List<Message>
                {
                    new Message { IsPrompt = false, Type = "Classic" },
                    new Message { IsPrompt = false, Type = "Classic" },
                    new Message { IsPrompt = false, Type = "Classic" }
                }
            };
            
            // Act
            var result = CopilotCreditEstimation.Analyze(auditEvent);
            
            // Assert
            Assert.AreEqual(3, result.ClassicAnswers);
            Assert.AreEqual(3, result.TotalCredits);
        }

        [TestMethod]
        public void CopilotCreditEstimation_Analyze_WithGenerativeAnswer_Returns2Credits()
        {
            // Arrange
            var auditEvent = new CopilotAuditEvent
            {
                Messages = new List<Message>
                {
                    new Message { IsPrompt = false, Type = "Generative" }
                }
            };
            
            // Act
            var result = CopilotCreditEstimation.Analyze(auditEvent);
            
            // Assert
            Assert.AreEqual(1, result.GenerativeAnswers);
            Assert.AreEqual(2, result.TotalCredits);
            Assert.IsTrue(result.CreditBreakdown.ContainsKey("Generative Answers"));
            Assert.AreEqual(2, result.CreditBreakdown["Generative Answers"]);
        }

        [TestMethod]
        public void CopilotCreditEstimation_Analyze_WithTenantGraphAnswer_Returns10Credits()
        {
            // Arrange
            var auditEvent = new CopilotAuditEvent
            {
                Messages = new List<Message>
                {
                    new Message { IsPrompt = false, Type = "TenantGraph" }
                }
            };
            
            // Act
            var result = CopilotCreditEstimation.Analyze(auditEvent);
            
            // Assert
            Assert.AreEqual(1, result.TenantGraphGroundedAnswers);
            Assert.AreEqual(10, result.TotalCredits);
            Assert.IsTrue(result.CreditBreakdown.ContainsKey("Tenant Graph Grounding"));
            Assert.AreEqual(10, result.CreditBreakdown["Tenant Graph Grounding"]);
        }

        [TestMethod]
        public void CopilotCreditEstimation_Analyze_WithPromptMessages_IgnoresPrompts()
        {
            // Arrange - Only prompt messages (user questions)
            var auditEvent = new CopilotAuditEvent
            {
                Messages = new List<Message>
                {
                    new Message { IsPrompt = true, Type = "Generative" },
                    new Message { IsPrompt = true, Type = "Classic" }
                }
            };
            
            // Act
            var result = CopilotCreditEstimation.Analyze(auditEvent);
            
            // Assert - Prompts should not be billed
            Assert.AreEqual(0, result.TotalCredits);
            Assert.AreEqual(0, result.GenerativeAnswers);
            Assert.AreEqual(0, result.ClassicAnswers);
        }

        [TestMethod]
        public void CopilotCreditEstimation_Analyze_WithMixedMessageTypes_CalculatesCorrectBreakdown()
        {
            // Arrange
            var auditEvent = new CopilotAuditEvent
            {
                Messages = new List<Message>
                {
                    new Message { IsPrompt = false, Type = "Classic" },
                    new Message { IsPrompt = false, Type = "Classic" },
                    new Message { IsPrompt = false, Type = "Generative" },
                    new Message { IsPrompt = false, Type = "TenantGraph" },
                    new Message { IsPrompt = true, Type = "Generative" } // Should be ignored
                }
            };
            
            // Act
            var result = CopilotCreditEstimation.Analyze(auditEvent);
            
            // Assert
            Assert.AreEqual(2, result.ClassicAnswers);
            Assert.AreEqual(1, result.GenerativeAnswers);
            Assert.AreEqual(1, result.TenantGraphGroundedAnswers);
            // Total: 2(1) + 1(2) + 1(10) = 14 credits
            Assert.AreEqual(14, result.TotalCredits);
        }

        #endregion

        #region Tenant Graph Inference Tests

        [TestMethod]
        public void CopilotCreditEstimation_Analyze_WithSharePointResource_InfersTenantGraphGrounding()
        {
            // Arrange - Message without explicit type, but with SharePoint resource
            var auditEvent = new CopilotAuditEvent
            {
                Messages = new List<Message>
                {
                    new Message { IsPrompt = false } // No explicit type
                },
                AccessedResources = new List<AccessedResource>
                {
                    new AccessedResource 
                    { 
                        Type = "docx",
                        SiteUrl = "https://contoso.sharepoint.com/sites/sales"
                    }
                }
            };
            
            // Act
            var result = CopilotCreditEstimation.Analyze(auditEvent);
            
            // Assert - Should infer tenant graph grounding
            Assert.AreEqual(1, result.TenantGraphGroundedAnswers);
            Assert.AreEqual(0, result.GenerativeAnswers);
            Assert.AreEqual(10, result.TotalCredits);
        }

        [TestMethod]
        public void CopilotCreditEstimation_Analyze_WithOneDriveResource_InfersTenantGraphGrounding()
        {
            // Arrange
            var auditEvent = new CopilotAuditEvent
            {
                Messages = new List<Message>
                {
                    new Message { IsPrompt = false }
                },
                AccessedResources = new List<AccessedResource>
                {
                    new AccessedResource 
                    { 
                        Type = "File",
                        SiteUrl = "https://contoso-my.sharepoint.com/personal/user"
                    }
                }
            };
            
            // Act
            var result = CopilotCreditEstimation.Analyze(auditEvent);
            
            // Assert
            Assert.AreEqual(1, result.TenantGraphGroundedAnswers);
            Assert.AreEqual(10, result.TotalCredits);
        }

        [TestMethod]
        public void CopilotCreditEstimation_Analyze_WithTeamsResource_InfersTenantGraphGrounding()
        {
            // Arrange
            var auditEvent = new CopilotAuditEvent
            {
                Messages = new List<Message>
                {
                    new Message { IsPrompt = false }
                },
                AccessedResources = new List<AccessedResource>
                {
                    new AccessedResource 
                    { 
                        Type = "TeamsMessage",
                        SiteUrl = "https://teams.microsoft.com"
                    }
                }
            };
            
            // Act
            var result = CopilotCreditEstimation.Analyze(auditEvent);
            
            // Assert
            Assert.AreEqual(1, result.TenantGraphGroundedAnswers);
            Assert.AreEqual(10, result.TotalCredits);
        }

        [TestMethod]
        public void CopilotCreditEstimation_Analyze_WithWebResourceOnly_InfersGenerativeAnswer()
        {
            // Arrange - Message without SharePoint/Graph resources
            var auditEvent = new CopilotAuditEvent
            {
                Messages = new List<Message>
                {
                    new Message { IsPrompt = false }
                },
                AccessedResources = new List<AccessedResource>
                {
                    new AccessedResource 
                    { 
                        Type = "WebPage",
                        SiteUrl = "https://www.example.com"
                    }
                }
            };
            
            // Act
            var result = CopilotCreditEstimation.Analyze(auditEvent);
            
            // Assert - Should be regular generative answer
            Assert.AreEqual(0, result.TenantGraphGroundedAnswers);
            Assert.AreEqual(1, result.GenerativeAnswers);
            Assert.AreEqual(2, result.TotalCredits);
        }

        [TestMethod]
        public void CopilotCreditEstimation_Analyze_WithNoResources_InfersGenerativeAnswer()
        {
            // Arrange
            var auditEvent = new CopilotAuditEvent
            {
                Messages = new List<Message>
                {
                    new Message { IsPrompt = false }
                }
            };
            
            // Act
            var result = CopilotCreditEstimation.Analyze(auditEvent);
            
            // Assert
            Assert.AreEqual(1, result.GenerativeAnswers);
            Assert.AreEqual(2, result.TotalCredits);
        }

        #endregion

        #region Agent Actions Tests

        [TestMethod]
        public void CopilotCreditEstimation_Analyze_WithAgentActions_Returns5CreditsPerAction()
        {
            // Arrange
            var auditEvent = new CopilotAuditEvent
            {
                AgentActions = new List<AgentAction>
                {
                    new AgentAction { Type = "Action" },
                    new AgentAction { Type = "Action" },
                    new AgentAction { Type = "Action" }
                }
            };
            
            // Act
            var result = CopilotCreditEstimation.Analyze(auditEvent);
            
            // Assert
            Assert.AreEqual(3, result.AgentActionCount);
            Assert.AreEqual(15, result.TotalCredits); // 3 actions × 5 credits
            Assert.IsTrue(result.CreditBreakdown.ContainsKey("Agent Actions"));
            Assert.AreEqual(15, result.CreditBreakdown["Agent Actions"]);
        }

        [TestMethod]
        public void CopilotCreditEstimation_Analyze_WithAISystemPlugin_InfersAgentActions()
        {
            // Arrange - Older audit log format
            var auditEvent = new CopilotAuditEvent
            {
                AISystemPlugin = new List<AISystemPlugin>
                {
                    new AISystemPlugin { Name = "BingWebSearch" },
                    new AISystemPlugin { Name = "GraphConnector" }
                }
            };
            
            // Act
            var result = CopilotCreditEstimation.Analyze(auditEvent);
            
            // Assert
            Assert.AreEqual(2, result.AgentActionCount);
            Assert.AreEqual(10, result.TotalCredits); // 2 plugins × 5 credits
        }

        #endregion

        #region AI Tool Usage Tests

        [TestMethod]
        public void CopilotCreditEstimation_Analyze_WithBasicAITools_CalculatesCorrectCredits()
        {
            // Arrange - 25 basic responses = 3 credits (ceiling of 25/10)
            var auditEvent = new CopilotAuditEvent
            {
                AIToolUsages = new List<AIToolUsage>
                {
                    new AIToolUsage 
                    { 
                        Tier = "Basic",
                        ResponseCount = 25 
                    }
                }
            };
            
            // Act
            var result = CopilotCreditEstimation.Analyze(auditEvent);
            
            // Assert
            Assert.AreEqual(25, result.BasicAIToolResponses);
            Assert.AreEqual(3, result.TotalCredits); // Ceiling(25/10) × 1
            Assert.AreEqual(3, result.CreditBreakdown["AI Tools (Basic)"]);
        }

        [TestMethod]
        public void CopilotCreditEstimation_Analyze_WithStandardAITools_CalculatesCorrectCredits()
        {
            // Arrange - 15 standard responses = 30 credits (ceiling of 15/10 = 2, × 15)
            var auditEvent = new CopilotAuditEvent
            {
                AIToolUsages = new List<AIToolUsage>
                {
                    new AIToolUsage 
                    { 
                        Tier = "Standard",
                        ResponseCount = 15 
                    }
                }
            };
            
            // Act
            var result = CopilotCreditEstimation.Analyze(auditEvent);
            
            // Assert
            Assert.AreEqual(15, result.StandardAIToolResponses);
            Assert.AreEqual(30, result.TotalCredits); // Ceiling(15/10) × 15
        }

        [TestMethod]
        public void CopilotCreditEstimation_Analyze_WithPremiumAITools_CalculatesCorrectCredits()
        {
            // Arrange - 8 premium responses = 100 credits (ceiling of 8/10 = 1, × 100)
            var auditEvent = new CopilotAuditEvent
            {
                AIToolUsages = new List<AIToolUsage>
                {
                    new AIToolUsage 
                    { 
                        Tier = "Premium",
                        ResponseCount = 8 
                    }
                }
            };
            
            // Act
            var result = CopilotCreditEstimation.Analyze(auditEvent);
            
            // Assert
            Assert.AreEqual(8, result.PremiumAIToolResponses);
            Assert.AreEqual(100, result.TotalCredits); // Ceiling(8/10) × 100
        }

        [TestMethod]
        public void CopilotCreditEstimation_Analyze_WithMultipleTiers_SumsCreditsCorrectly()
        {
            // Arrange
            var auditEvent = new CopilotAuditEvent
            {
                AIToolUsages = new List<AIToolUsage>
                {
                    new AIToolUsage { Tier = "Basic", ResponseCount = 15 },     // 2 credits
                    new AIToolUsage { Tier = "Standard", ResponseCount = 10 },  // 15 credits
                    new AIToolUsage { Tier = "Premium", ResponseCount = 5 }     // 100 credits
                }
            };
            
            // Act
            var result = CopilotCreditEstimation.Analyze(auditEvent);
            
            // Assert
            Assert.AreEqual(15, result.BasicAIToolResponses);
            Assert.AreEqual(10, result.StandardAIToolResponses);
            Assert.AreEqual(5, result.PremiumAIToolResponses);
            Assert.AreEqual(117, result.TotalCredits); // 2 + 15 + 100
        }

        #endregion

        #region Flow Actions Tests

        [TestMethod]
        public void CopilotCreditEstimation_Analyze_WithFlowActions_CalculatesCorrectCredits()
        {
            // Arrange - 250 actions = 39 credits (ceiling of 250/100 × 13)
            var auditEvent = new CopilotAuditEvent
            {
                FlowActions = new AgentFlowUsage
                {
                    ActionCount = 250
                }
            };
            
            // Act
            var result = CopilotCreditEstimation.Analyze(auditEvent);
            
            // Assert
            Assert.AreEqual(250, result.FlowActions);
            Assert.AreEqual(39, result.TotalCredits); // Ceiling(250/100) × 13
            Assert.AreEqual(39, result.CreditBreakdown["Agent Flow Actions"]);
        }

        [TestMethod]
        public void CopilotCreditEstimation_Analyze_WithSmallFlowActionCount_RoundsUpTo13Credits()
        {
            // Arrange - Even 1 action should round up to 13 credits
            var auditEvent = new CopilotAuditEvent
            {
                FlowActions = new AgentFlowUsage { ActionCount = 1 }
            };
            
            // Act
            var result = CopilotCreditEstimation.Analyze(auditEvent);
            
            // Assert
            Assert.AreEqual(13, result.TotalCredits); // Ceiling(1/100) × 13 = 1 × 13
        }

        #endregion

        #region Complex Scenario Tests

        [TestMethod]
        public void CopilotCreditEstimation_Analyze_WithCompleteScenario_CalculatesAllCredits()
        {
            // Arrange - Realistic complex scenario
            var auditEvent = new CopilotAuditEvent
            {
                Messages = new List<Message>
                {
                    new Message { IsPrompt = false, Type = "Classic" },    // 1 credit
                    new Message { IsPrompt = false, Type = "Generative" }, // 2 credits
                    new Message { IsPrompt = false, Type = "TenantGraph" } // 10 credits
                },
                AgentActions = new List<AgentAction>
                {
                    new AgentAction { Type = "Action" },
                    new AgentAction { Type = "Action" }  // 2 × 5 = 10 credits
                },
                AIToolUsages = new List<AIToolUsage>
                {
                    new AIToolUsage { Tier = "Basic", ResponseCount = 10 },    // 1 credit
                    new AIToolUsage { Tier = "Standard", ResponseCount = 20 }, // 30 credits
                    new AIToolUsage { Tier = "Premium", ResponseCount = 5 }    // 100 credits
                },
                FlowActions = new AgentFlowUsage { ActionCount = 150 },  // 26 credits
                AccessedResources = new List<AccessedResource>
                {
                    new AccessedResource { Type = "docx", SiteUrl = "https://contoso.sharepoint.com" },
                    new AccessedResource { Type = "xlsx", SiteUrl = "https://contoso.sharepoint.com" }
                }
            };
            
            // Act
            var result = CopilotCreditEstimation.Analyze(auditEvent);
            
            // Assert - Verify totals
            Assert.AreEqual(1, result.ClassicAnswers);
            Assert.AreEqual(1, result.GenerativeAnswers);
            Assert.AreEqual(1, result.TenantGraphGroundedAnswers);
            Assert.AreEqual(2, result.AgentActionCount);
            Assert.AreEqual(10, result.BasicAIToolResponses);
            Assert.AreEqual(20, result.StandardAIToolResponses);
            Assert.AreEqual(5, result.PremiumAIToolResponses);
            Assert.AreEqual(150, result.FlowActions);
            
            // Total: 1 + 2 + 10 + 10 + 1 + 30 + 100 + 26 = 180 credits
            Assert.AreEqual(180, result.TotalCredits);
            
            // Verify breakdown
            Assert.AreEqual(1, result.CreditBreakdown["Classic Answers"]);
            Assert.AreEqual(2, result.CreditBreakdown["Generative Answers"]);
            Assert.AreEqual(10, result.CreditBreakdown["Tenant Graph Grounding"]);
            Assert.AreEqual(10, result.CreditBreakdown["Agent Actions"]);
            Assert.AreEqual(1, result.CreditBreakdown["AI Tools (Basic)"]);
            Assert.AreEqual(30, result.CreditBreakdown["AI Tools (Standard)"]);
            Assert.AreEqual(100, result.CreditBreakdown["AI Tools (Premium)"]);
            Assert.AreEqual(26, result.CreditBreakdown["Agent Flow Actions"]);
            
            // Verify resource breakdown (informational only)
            Assert.IsTrue(result.ResourceTypeBreakdown.ContainsKey("docx"));
            Assert.IsTrue(result.ResourceTypeBreakdown.ContainsKey("xlsx"));
        }

        [TestMethod]
        public void CopilotCreditEstimation_Analyze_WithResourceTypeBreakdown_CountsCorrectly()
        {
            // Arrange
            var auditEvent = new CopilotAuditEvent
            {
                Messages = new List<Message>
                {
                    new Message { IsPrompt = false }
                },
                AccessedResources = new List<AccessedResource>
                {
                    new AccessedResource { Type = "docx" },
                    new AccessedResource { Type = "docx" },
                    new AccessedResource { Type = "xlsx" },
                    new AccessedResource { Type = "pptx" },
                    new AccessedResource { Type = "" } // Should be counted as "WebPage"
                }
            };
            
            // Act
            var result = CopilotCreditEstimation.Analyze(auditEvent);
            
            // Assert
            Assert.AreEqual(2, result.ResourceTypeBreakdown["docx"]);
            Assert.AreEqual(1, result.ResourceTypeBreakdown["xlsx"]);
            Assert.AreEqual(1, result.ResourceTypeBreakdown["pptx"]);
            Assert.AreEqual(1, result.ResourceTypeBreakdown["WebPage"]);
        }

        #endregion

        #region Case Insensitivity Tests

        [TestMethod]
        public void CopilotCreditEstimation_Analyze_WithCaseInsensitiveMessageTypes_HandlesCorrectly()
        {
            // Arrange
            var auditEvent = new CopilotAuditEvent
            {
                Messages = new List<Message>
                {
                    new Message { IsPrompt = false, Type = "CLASSIC" },
                    new Message { IsPrompt = false, Type = "generative" },
                    new Message { IsPrompt = false, Type = "TenantGraph" }
                }
            };
            
            // Act
            var result = CopilotCreditEstimation.Analyze(auditEvent);
            
            // Assert
            Assert.AreEqual(1, result.ClassicAnswers);
            Assert.AreEqual(1, result.GenerativeAnswers);
            Assert.AreEqual(1, result.TenantGraphGroundedAnswers);
            Assert.AreEqual(13, result.TotalCredits); // 1 + 2 + 10
        }

        [TestMethod]
        public void CopilotCreditEstimation_Analyze_WithCaseInsensitiveTiers_HandlesCorrectly()
        {
            // Arrange
            var auditEvent = new CopilotAuditEvent
            {
                AIToolUsages = new List<AIToolUsage>
                {
                    new AIToolUsage { Tier = "BASIC", ResponseCount = 10 },
                    new AIToolUsage { Tier = "standard", ResponseCount = 10 },
                    new AIToolUsage { Tier = "Premium", ResponseCount = 10 }
                }
            };
            
            // Act
            var result = CopilotCreditEstimation.Analyze(auditEvent);
            
            // Assert
            Assert.AreEqual(10, result.BasicAIToolResponses);
            Assert.AreEqual(10, result.StandardAIToolResponses);
            Assert.AreEqual(10, result.PremiumAIToolResponses);
        }

        #endregion
    }
}
