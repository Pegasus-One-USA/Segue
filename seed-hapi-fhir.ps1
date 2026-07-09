$baseUrl = "http://localhost:8080/fhir"

for ($i = 1; $i -le 100; $i++) {

    $patient = @{
        resourceType = "Patient"
        identifier = @(
            @{
                system = "http://fhirbridge.local/mrn"
                value = "MRN-$i"
            }
        )
        name = @(
            @{
                family = "Patient$i"
                given = @("Test")
            }
        )
        gender = if ($i % 2 -eq 0) { "male" } else { "female" }
        birthDate = "1990-01-01"
    } | ConvertTo-Json -Depth 10

    $createdPatient = Invoke-RestMethod `
        -Method Post `
        -Uri "$baseUrl/Patient" `
        -ContentType "application/fhir+json" `
        -Body $patient

    $patientId = $createdPatient.id
    Write-Host "Created Patient/$patientId"

    $encounter = @{
        resourceType = "Encounter"
        status = "finished"
        class = @{
            system = "http://terminology.hl7.org/CodeSystem/v3-ActCode"
            code = "AMB"
            display = "ambulatory"
        }
        subject = @{
            reference = "Patient/$patientId"
        }
    } | ConvertTo-Json -Depth 10

    $createdEncounter = Invoke-RestMethod `
        -Method Post `
        -Uri "$baseUrl/Encounter" `
        -ContentType "application/fhir+json" `
        -Body $encounter

    $encounterId = $createdEncounter.id

    $condition = @{
        resourceType = "Condition"
        clinicalStatus = @{
            coding = @(
                @{
                    system = "http://terminology.hl7.org/CodeSystem/condition-clinical"
                    code = "active"
                }
            )
        }
        code = @{
            text = "Hypertension"
        }
        subject = @{
            reference = "Patient/$patientId"
        }
        encounter = @{
            reference = "Encounter/$encounterId"
        }
    } | ConvertTo-Json -Depth 10

    Invoke-RestMethod `
        -Method Post `
        -Uri "$baseUrl/Condition" `
        -ContentType "application/fhir+json" `
        -Body $condition

    $medicationRequest = @{
        resourceType = "MedicationRequest"
        status = "active"
        intent = "order"
        medicationCodeableConcept = @{
            text = "Amlodipine 5mg"
        }
        subject = @{
            reference = "Patient/$patientId"
        }
        encounter = @{
            reference = "Encounter/$encounterId"
        }
    } | ConvertTo-Json -Depth 10

    Invoke-RestMethod `
        -Method Post `
        -Uri "$baseUrl/MedicationRequest" `
        -ContentType "application/fhir+json" `
        -Body $medicationRequest

    $observation = @{
        resourceType = "Observation"
        status = "final"
        code = @{
            text = "Blood Pressure"
        }
        subject = @{
            reference = "Patient/$patientId"
        }
        encounter = @{
            reference = "Encounter/$encounterId"
        }
        valueString = "120/80"
    } | ConvertTo-Json -Depth 10

    Invoke-RestMethod `
        -Method Post `
        -Uri "$baseUrl/Observation" `
        -ContentType "application/fhir+json" `
        -Body $observation
}