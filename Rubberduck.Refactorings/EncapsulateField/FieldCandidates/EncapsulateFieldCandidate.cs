﻿using Rubberduck.Parsing.Grammar;
using Rubberduck.Parsing.Symbols;
using Rubberduck.Refactorings.Common;
using Rubberduck.Refactorings.EncapsulateField.Extensions;
using Rubberduck.VBEditor;

namespace Rubberduck.Refactorings.EncapsulateField
{
    public interface IEncapsulateFieldRefactoringElement
    {
        string IdentifierName { get; }
        QualifiedModuleName QualifiedModuleName { get; }
        string AsTypeName { get; }
    }

    public interface IEncapsulateFieldCandidate : IEncapsulateFieldRefactoringElement
    {
        string TargetID { get; }
        Declaration Declaration { get; }
        bool EncapsulateFlag { get; set; }
        string BackingIdentifier { set; get; }
        string BackingAsTypeName { get; }
        string PropertyIdentifier { set; get; }
        string PropertyAsTypeName { get; }
        bool CanBeReadWrite { set; get; }
        bool ImplementLet { get; }
        bool ImplementSet { get; }
        bool IsReadOnly { set; get; }
        IEncapsulateFieldConflictFinder ConflictFinder { set; get; }
        bool TryValidateEncapsulationAttributes(out string errorMessage);
    }

    public class EncapsulateFieldCandidate : IEncapsulateFieldCandidate
    {
        protected Declaration _target;
        protected QualifiedModuleName _qmn;
        protected readonly string _uniqueID;
        protected int _hashCode;
        private string _identifierName;
        protected EncapsulationIdentifiers _fieldAndProperty;
        private string _rhsParameterIdentifierName;

        public EncapsulateFieldCandidate(Declaration declaration)
        {
            _target = declaration;
            _rhsParameterIdentifierName = Resources.Refactorings.Refactorings.CodeBuilder_DefaultPropertyRHSParam;

            _fieldAndProperty = new EncapsulationIdentifiers(declaration.IdentifierName);
            IdentifierName = declaration.IdentifierName;
            PropertyAsTypeName = declaration.AsTypeName;
            _qmn = declaration.QualifiedModuleName;

            CanBeReadWrite = true;

            _uniqueID = $"{_qmn.Name}.{declaration.IdentifierName}";
            _hashCode = _uniqueID.GetHashCode();

            ImplementLet = true;
            ImplementSet = false;
            if (_target.IsEnumField() && _target.AsTypeDeclaration.HasPrivateAccessibility())
            {
                //5.3.1 The declared type of a function declaration may not be a private enum.
                PropertyAsTypeName = Tokens.Long;
            }
            else if (_target.AsTypeName.Equals(Tokens.Variant)
                && !_target.IsArray)
            {
                ImplementSet = true;
            }
            else if (Declaration.IsObject)
            {
                ImplementLet = false;
                ImplementSet = true;
            }
        }

        public Declaration Declaration => _target;

        public string AsTypeName => _target.AsTypeName;

        public virtual string BackingIdentifier
        {
            get => _fieldAndProperty.Field;
            set => _fieldAndProperty.Field = value;
        }

        public string BackingAsTypeName => Declaration.AsTypeName;

        public virtual IEncapsulateFieldConflictFinder ConflictFinder { set; get; }

        public virtual bool TryValidateEncapsulationAttributes(out string errorMessage)
        {
            (bool IsValid, string ErrorMsg) = ConflictFinder?.ValidateEncapsulationAttributes(this) ?? (true, string.Empty);

            errorMessage = ErrorMsg;
            return IsValid;
        }

        public virtual string TargetID => _target?.IdentifierName ?? IdentifierName;

        protected bool _encapsulateFlag;
        public virtual bool EncapsulateFlag
        {
            set
            {
                if (_encapsulateFlag == value)
                {
                    return;
                }

                _encapsulateFlag = value;
                if (!_encapsulateFlag)
                {
                    PropertyIdentifier = _fieldAndProperty.DefaultPropertyName;
                    return;
                }

                ConflictFinder?.AssignNoConflictIdentifiers(this);
            }
            get => _encapsulateFlag;
        }

        public virtual bool IsReadOnly { set; get; }

        public bool CanBeReadWrite { set; get; }

        public string PropertyIdentifier
        {
            get => _fieldAndProperty.Property;
            set
            {
                _fieldAndProperty.Property = value;

                TryRestoreNewFieldNameAsOriginalFieldIdentifierName();
            }
        }

        private void TryRestoreNewFieldNameAsOriginalFieldIdentifierName()
        {
            var canNowUseOriginalFieldName = !_fieldAndProperty.TargetFieldName.IsEquivalentVBAIdentifierTo(_fieldAndProperty.Property)
                && !(ConflictFinder?.IsConflictingIdentifier(this, _fieldAndProperty.TargetFieldName, out _) ?? false);

            if (canNowUseOriginalFieldName)
            {
                _fieldAndProperty.Field = _fieldAndProperty.TargetFieldName;
                return;
            }

            if (_fieldAndProperty.Field.IsEquivalentVBAIdentifierTo(_fieldAndProperty.TargetFieldName))
            {
                _fieldAndProperty.Field = _fieldAndProperty.DefaultNewFieldName;
                ConflictFinder?.AssignNoConflictIdentifiers(this);
            }
        }

        public string PropertyAsTypeName { get; set; }

        public QualifiedModuleName QualifiedModuleName => _qmn;

        public string IdentifierName
        {
            get => Declaration?.IdentifierName ?? _identifierName;
            set => _identifierName = value;
        }

        private bool _implLet;
        public bool ImplementLet
        {
            get => !IsReadOnly && _implLet;
            set => _implLet = value;
        }

        private bool _implSet;
        public bool ImplementSet
        {
            get => !IsReadOnly && _implSet;
            set => _implSet = value;
        }

        public override bool Equals(object obj)
        {
            return obj != null
                && obj is IEncapsulateFieldCandidate efc
                && $"{efc.QualifiedModuleName.Name}.{efc.IdentifierName}" == _uniqueID;
        }

        public override int GetHashCode() => _hashCode;

        public override string ToString()
            => $"({TargetID}){Declaration.ToString()}";
    }
}
