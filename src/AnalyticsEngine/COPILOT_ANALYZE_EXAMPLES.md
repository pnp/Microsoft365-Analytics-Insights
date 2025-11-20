# Example: Using CopilotCreditEstimation.Analyze Overloads

## Unit Test Examples

### Example 1: Testing Message Type Credits (Object Overload)

```csharp
[TestClass]
public class CopilotCreditEstimationTests
{
    [TestMethod]
    public void Analyze_WithClassicAnswer_Returns1Credit()
    {
        // Arrange - Build object directly
        var auditEvent = new CopilotAuditEvent
        {
            Messages = new List<Message>
            {
                new Message 
                { 
                    IsPrompt = false,
                    Type = "Classic" 
                }
            }
        };
        
        // Act - Use object overload
        var result = CopilotCreditEstimation.Analyze(auditEvent);
        
        // Assert
        Assert.AreEqual(1, result.ClassicAnswers);
        Assert.AreEqual(1, result.TotalCredits);
        Assert.AreEqual(1, result.CreditBreakdown["Classic Answers"]);
    }

    [TestMethod]
    public void Analyze_WithGenerativeAnswer_Returns2Credits()
    {
        // Arrange
        var auditEvent = new CopilotAuditEvent
        {
            Messages = new List<Message>
            {
                new Message 
                { 
                    IsPrompt = false,
                    Type = "Generative" 
                }
            }
        };
        
        // Act
        var result = CopilotCreditEstimation.Analyze(auditEvent);
        
        // Assert
        Assert.AreEqual(1, result.GenerativeAnswers);
        Assert.AreEqual(2, result.TotalCredits);
    }

    [TestMethod]
    public void Analyze_WithTenantGraphAnswer_Returns10Credits()
    {
        // Arrange
        var auditEvent = new CopilotAuditEvent
        {
            Messages = new List<Message>
            {
                new Message 
                { 
                    IsPrompt = false,
                    Type = "TenantGraph" 
                }
            }
        };
        
        // Act
        var result = CopilotCreditEstimation.Analyze(auditEvent);
        
        // Assert
        Assert.AreEqual(1, result.TenantGraphGroundedAnswers);
        Assert.AreEqual(10, result.TotalCredits);
    }
}
```

### Example 2: Testing Tenant Graph Inference

```csharp
[TestMethod]
public void Analyze_WithSharePointResource_InfersTenantGraphGrounding()
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
public void Analyze_WithWebResourceOnly_InfersGenerativeAnswer()
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
```

### Example 3: Testing Agent Actions

```csharp
[TestMethod]
public void Analyze_WithAgentActions_Returns5CreditsPerAction()
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
    Assert.AreEqual(15, result.CreditBreakdown["Agent Actions"]);
}

[TestMethod]
public void Analyze_WithAISystemPlugin_InfersAgentActions()
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
```

### Example 4: Testing AI Tool Usages

```csharp
[TestMethod]
public void Analyze_WithBasicAITools_CalculatesCorrectCredits()
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
public void Analyze_WithMultipleTiers_SumsCreditsCorrectly()
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
```

### Example 5: Testing Flow Actions

```csharp
[TestMethod]
public void Analyze_WithFlowActions_CalculatesCorrectCredits()
{
    // Arrange - 250 actions = 39 credits (ceiling of 250/100 × 13)
    var auditEvent = new CopilotAuditEvent
    {
        FlowActions = new FlowAction
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
public void Analyze_WithSmallFlowActionCount_RoundsUpTo13Credits()
{
    // Arrange - Even 1 action should round up to 13 credits
    var auditEvent = new CopilotAuditEvent
    {
        FlowActions = new FlowAction { ActionCount = 1 }
    };
    
    // Act
    var result = CopilotCreditEstimation.Analyze(auditEvent);
    
    // Assert
    Assert.AreEqual(13, result.TotalCredits); // Ceiling(1/100) × 13 = 1 × 13
}
```

### Example 6: Complex Scenario Testing

```csharp
[TestMethod]
public void Analyze_WithCompleteScenario_CalculatesAllCredits()
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
        FlowActions = new FlowAction { ActionCount = 150 },  // 26 credits
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
```

### Example 7: Edge Cases and Null Handling

```csharp
[TestMethod]
public void Analyze_WithNullJson_ReturnsEmptyReport()
{
    // Act
    var result = CopilotCreditEstimation.Analyze((string)null);
    
    // Assert
    Assert.AreEqual(0, result.TotalCredits);
    Assert.IsNotNull(result.ResourceTypeBreakdown);
    Assert.IsNotNull(result.CreditBreakdown);
}

[TestMethod]
public void Analyze_WithEmptyJson_ReturnsEmptyReport()
{
    // Act
    var result = CopilotCreditEstimation.Analyze("");
    
    // Assert
    Assert.AreEqual(0, result.TotalCredits);
}

[TestMethod]
public void Analyze_WithNullObject_ReturnsEmptyReport()
{
    // Act
    var result = CopilotCreditEstimation.Analyze((CopilotAuditEvent)null);
    
    // Assert
    Assert.AreEqual(0, result.TotalCredits);
    Assert.IsNotNull(result.ResourceTypeBreakdown);
    Assert.IsNotNull(result.CreditBreakdown);
}

[TestMethod]
public void Analyze_WithEmptyAuditEvent_ReturnsZeroCredits()
{
    // Arrange
    var auditEvent = new CopilotAuditEvent();
    
    // Act
    var result = CopilotCreditEstimation.Analyze(auditEvent);
    
    // Assert
    Assert.AreEqual(0, result.TotalCredits);
    Assert.AreEqual(0, result.ClassicAnswers);
    Assert.AreEqual(0, result.GenerativeAnswers);
    Assert.AreEqual(0, result.AgentActionCount);
}

[TestMethod]
public void Analyze_WithPromptMessagesOnly_IgnoresPrompts()
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
```

### Example 8: Data-Driven Testing

```csharp
[TestClass]
public class CopilotCreditEstimationDataDrivenTests
{
    [TestMethod]
    [DataRow(1, 0, 0, 1, DisplayName = "1 Classic = 1 credit")]
    [DataRow(0, 1, 0, 2, DisplayName = "1 Generative = 2 credits")]
    [DataRow(0, 0, 1, 10, DisplayName = "1 TenantGraph = 10 credits")]
    [DataRow(2, 0, 0, 2, DisplayName = "2 Classic = 2 credits")]
    [DataRow(0, 3, 0, 6, DisplayName = "3 Generative = 6 credits")]
    [DataRow(1, 1, 1, 13, DisplayName = "Mixed: 1+2+10 = 13 credits")]
    public void Analyze_MessageCombinations_CalculatesCorrectCredits(
        int classicCount, 
        int generativeCount, 
        int tenantGraphCount, 
        int expectedCredits)
    {
        // Arrange
        var messages = new List<Message>();
        
        for (int i = 0; i < classicCount; i++)
            messages.Add(new Message { IsPrompt = false, Type = "Classic" });
        
        for (int i = 0; i < generativeCount; i++)
            messages.Add(new Message { IsPrompt = false, Type = "Generative" });
        
        for (int i = 0; i < tenantGraphCount; i++)
            messages.Add(new Message { IsPrompt = false, Type = "TenantGraph" });
        
        var auditEvent = new CopilotAuditEvent { Messages = messages };
        
        // Act
        var result = CopilotCreditEstimation.Analyze(auditEvent);
        
        // Assert
        Assert.AreEqual(expectedCredits, result.TotalCredits);
        Assert.AreEqual(classicCount, result.ClassicAnswers);
        Assert.AreEqual(generativeCount, result.GenerativeAnswers);
        Assert.AreEqual(tenantGraphCount, result.TenantGraphGroundedAnswers);
    }
}
```

## Production Usage Examples

### Example 1: Processing Audit Logs from API

```csharp
public class CopilotAuditProcessor
{
    public async Task ProcessAuditLogs()
    {
        // Get raw JSON from Microsoft 365 Management API
        var auditLogs = await GetCopilotAuditLogsFromAPI();
        
        foreach (var logJson in auditLogs)
        {
            // Use JSON string overload
            var estimate = CopilotCreditEstimation.Analyze(logJson);
            
            await SaveToDatabase(estimate);
        }
    }
}
```

### Example 2: Internal Processing with Deserialized Objects

```csharp
public class CopilotEventManager
{
    public async Task ProcessEvent(CopilotAuditLogContent auditLogContent)
    {
        // Already have deserialized audit event
        var copilotEvent = BuildCopilotAuditEvent(auditLogContent);
        
        // Use object overload - no need to serialize/deserialize
        var estimate = CopilotCreditEstimation.Analyze(copilotEvent);
        
        // Store extended data
        await SaveExtendedData(copilotEvent, estimate);
    }
    
    private CopilotAuditEvent BuildCopilotAuditEvent(CopilotAuditLogContent content)
    {
        return new CopilotAuditEvent
        {
            Messages = content.Messages,
            AccessedResources = content.CopilotEventData?.AccessedResources,
            AgentActions = content.AgentActions,
            AIToolUsages = content.AIToolUsages,
            FlowActions = content.FlowActions
        };
    }
}
```

### Example 3: Batch Analysis

```csharp
public class CopilotAnalytics
{
    public async Task<CreditUsageReport> AnalyzeTenantUsage(DateTime from, DateTime to)
    {
        var events = await LoadCopilotEvents(from, to);
        var estimates = new List<CopilotCreditEstimation>();
        
        foreach (var evt in events)
        {
            // Use object overload for better performance
            var estimate = CopilotCreditEstimation.Analyze(evt);
            estimates.Add(estimate);
        }
        
        return new CreditUsageReport
        {
            TotalCredits = estimates.Sum(e => e.TotalCredits),
            ClassicAnswers = estimates.Sum(e => e.ClassicAnswers),
            GenerativeAnswers = estimates.Sum(e => e.GenerativeAnswers),
            TenantGraphAnswers = estimates.Sum(e => e.TenantGraphGroundedAnswers),
            // ... more aggregations
        };
    }
}
```

## Comparison: JSON vs Object Overload

### When to Use JSON Overload
```csharp
? var json = await httpClient.GetStringAsync(apiUrl);
   var estimate = CopilotCreditEstimation.Analyze(json);

? var json = File.ReadAllText("audit-log.json");
   var estimate = CopilotCreditEstimation.Analyze(json);

? var json = GetFromCache();
   var estimate = CopilotCreditEstimation.Analyze(json);
```

### When to Use Object Overload
```csharp
? var auditEvent = new CopilotAuditEvent { /* test data */ };
   var estimate = CopilotCreditEstimation.Analyze(auditEvent);

? var auditEvent = await LoadFromDatabase(id);
   var estimate = CopilotCreditEstimation.Analyze(auditEvent);

? var auditEvent = BuildTestScenario();
   var estimate = CopilotCreditEstimation.Analyze(auditEvent);
```

## Helper Methods for Testing

```csharp
public static class CopilotTestHelpers
{
    public static CopilotAuditEvent CreateEventWithMessages(
        int classicCount, 
        int generativeCount, 
        int tenantGraphCount)
    {
        var messages = new List<Message>();
        
        for (int i = 0; i < classicCount; i++)
            messages.Add(new Message { IsPrompt = false, Type = "Classic" });
        
        for (int i = 0; i < generativeCount; i++)
            messages.Add(new Message { IsPrompt = false, Type = "Generative" });
        
        for (int i = 0; i < tenantGraphCount; i++)
            messages.Add(new Message { IsPrompt = false, Type = "TenantGraph" });
        
        return new CopilotAuditEvent { Messages = messages };
    }
    
    public static CopilotAuditEvent CreateEventWithTenantGraphResources()
    {
        return new CopilotAuditEvent
        {
            Messages = new List<Message>
            {
                new Message { IsPrompt = false }
            },
            AccessedResources = new List<AccessedResource>
            {
                new AccessedResource 
                { 
                    Type = "docx",
                    SiteUrl = "https://tenant.sharepoint.com"
                }
            }
        };
    }
}
```
