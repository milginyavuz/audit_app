# Audit App

Audit App is a .NET/WPF desktop application developed to support **audit and financial data analysis** by converting raw ledger inputs into structured and exportable datasets.

The application focuses on **automating audit workflows**, improving data consistency, and enabling efficient analysis of large transaction volumes.

> Project status: **Under active development**

---

## Purpose

Audit and finance teams often receive ledger data in raw formats such as `.txt` and `.xml`.  
Muavin Desktop aims to parse, normalize, and structure these inputs into analyzable tables and export them to Excel for audit working papers.

---

## Current Features

- Import ledger data from **TXT** and **XML** files  
- Transaction and ledger (muavin) analysis  
- Drill-down to entry-level transaction details  
- Aging / maturity analysis based on transaction dates  
- Export processed tables to **Excel**  
- Debit / credit logic and audit-oriented data modeling  

---

## Tech Stack

- C#
- .NET (WPF)
- MVVM architecture
- Custom TXT & XML parsers
- Excel export utilities

---

## Repository Structure

- `Muavin.Desktop` – WPF UI layer  
- `Muavin.Loader` – Data loading and orchestration  
- `Muavin.Xml` – XML parsing and normalization logic  

---

## Development Status

The application is currently under development and not yet production-ready.

Planned improvements include:
- Validation and consistency checks for ledger data  
- Improved error handling for malformed inputs  
- Performance optimization for large datasets  
- Enhanced Excel export templates  
- UI and usability refinements  

---

## Usage

1. Open `Muavin.Desktop.sln` in Visual Studio  
2. Set `Muavin.Desktop` as startup project  
3. Run the application and load `.txt` or `.xml` input files  
4. Analyze and export results to Excel  
