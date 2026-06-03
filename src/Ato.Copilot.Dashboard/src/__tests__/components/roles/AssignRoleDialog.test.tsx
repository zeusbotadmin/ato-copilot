import { render, screen, fireEvent, waitFor, act } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';

// ─────────────────────────────────────────────────────────────────────────────
// T033 [US2] — Failing test pinning the AssignRoleDialog contract.
//
// Drives FR-008, FR-014, FR-026, FR-027:
//   (a) Role dropdown filters by RBAC_ASSIGNABLE_BY[callerEffectiveRole].
//   (b) lockRole=true disables the role dropdown.
//   (c) SoD warning from the response renders inline.
//   (d) bootstrap prop wires bootstrap=true into the POST body.
// ─────────────────────────────────────────────────────────────────────────────

// Mock the rolesApi module BEFORE importing the dialog.
vi.mock('../../../api/roles', async (orig) => {
  const actual = await orig<typeof import('../../../api/roles')>();
  return {
    ...actual,
    rolesApi: {
      assignOrgRole: vi.fn(),
      removeOrgRole: vi.fn(),
      assignSystemRole: vi.fn(),
      removeSystemRole: vi.fn(),
      getSystemRoles: vi.fn(),
      getEffectiveRole: vi.fn(),
    },
  };
});

import AssignRoleDialog from '../../../components/roles/AssignRoleDialog';
import { rolesApi } from '../../../api/roles';

const mockedApi = rolesApi as unknown as {
  assignOrgRole: ReturnType<typeof vi.fn>;
  assignSystemRole: ReturnType<typeof vi.fn>;
};

describe('AssignRoleDialog', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('filters role dropdown by RBAC_ASSIGNABLE_BY for Isso callers', () => {
    // Arrange — ISSO callers may assign ONLY MissionOwner and SystemOwner.
    render(
      <AssignRoleDialog
        open
        onClose={vi.fn()}
        scope={{ kind: 'organization' }}
        callerEffectiveRole="Isso"
        onAssigned={vi.fn()}
      />,
    );

    // Act
    const dropdown = screen.getByLabelText(/role/i) as HTMLSelectElement;
    const optionValues = Array.from(dropdown.options).map((o) => o.value);

    // Assert — ISSO may assign only MissionOwner + SystemOwner
    expect(optionValues).toEqual(expect.arrayContaining(['MissionOwner', 'SystemOwner']));
    expect(optionValues).not.toEqual(expect.arrayContaining(['AuthorizingOfficial']));
    expect(optionValues).not.toEqual(expect.arrayContaining(['Issm']));
    expect(optionValues).not.toEqual(expect.arrayContaining(['Sca']));
    expect(optionValues).not.toEqual(expect.arrayContaining(['Administrator']));
  });

  it('disables role dropdown when lockRole=true and pre-selects initialRole', () => {
    // Arrange
    render(
      <AssignRoleDialog
        open
        onClose={vi.fn()}
        scope={{ kind: 'organization' }}
        initialRole="MissionOwner"
        lockRole
        callerEffectiveRole="Issm"
        onAssigned={vi.fn()}
      />,
    );

    // Act
    const dropdown = screen.getByLabelText(/role/i) as HTMLSelectElement;

    // Assert
    expect(dropdown.disabled).toBe(true);
    expect(dropdown.value).toBe('MissionOwner');
  });

  it('renders inline SoD warning when server response contains one', async () => {
    // Arrange — server returns success with SoD warning
    mockedApi.assignOrgRole.mockResolvedValueOnce({
      status: 'success',
      data: {
        role: 'Issm',
        person: { id: 'p-1', displayName: 'Conflicted Carol' },
        source: 'override',
      },
      warnings: [
        {
          code: 'SOD_VIOLATION',
          message:
            'Person already holds AuthorizingOfficial; assigning Issm would violate DoDI 8510.01 separation of duties.',
          roleConflict: ['AuthorizingOfficial', 'Issm'],
          dodiReference: 'DoDI 8510.01 Enclosure 3 § 4.b',
          suggestedAction: 'Assign Issm to a different person.',
        },
      ],
    });
    const onAssigned = vi.fn();

    render(
      <AssignRoleDialog
        open
        onClose={vi.fn()}
        scope={{ kind: 'organization' }}
        initialRole="Issm"
        callerEffectiveRole="Issm"
        onAssigned={onAssigned}
      />,
    );

    // Provide person id + click Assign
    const personInput = screen.getByLabelText(/person/i) as HTMLInputElement;
    fireEvent.change(personInput, { target: { value: '11111111-1111-1111-1111-111111111111' } });

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /assign/i }));
    });

    // Assert — the SoD warning is visible inline. We use the unique part of
    // the dodiReference string ("Enclosure 3") to disambiguate from the
    // matching substring in the user-facing message.
    await waitFor(() => {
      expect(screen.getByText(/DoDI 8510\.01 Enclosure/i)).toBeInTheDocument();
    });
    expect(screen.getByText(/violate.*separation of duties/i)).toBeInTheDocument();
    expect(onAssigned).toHaveBeenCalled();
  });

  it('passes bootstrap=true in the Org-role POST body when prop is set', async () => {
    // Arrange
    mockedApi.assignOrgRole.mockResolvedValueOnce({
      status: 'success',
      data: { role: 'Administrator', person: { id: 'p-1', displayName: 'A' }, source: 'override' },
    });
    render(
      <AssignRoleDialog
        open
        onClose={vi.fn()}
        scope={{ kind: 'organization' }}
        initialRole="Administrator"
        lockRole
        callerEffectiveRole={null}
        bootstrap
        onAssigned={vi.fn()}
      />,
    );

    // Act — fill person + submit
    const personInput = screen.getByLabelText(/person/i) as HTMLInputElement;
    fireEvent.change(personInput, { target: { value: '22222222-2222-2222-2222-222222222222' } });
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /assign/i }));
    });

    // Assert — POST body included bootstrap: true
    await waitFor(() => {
      expect(mockedApi.assignOrgRole).toHaveBeenCalledWith(
        expect.objectContaining({ bootstrap: true, role: 'Administrator' }),
      );
    });
  });

  it('renders inline error block when server returns 403 RBAC_ROLE_ASSIGN_DENIED', async () => {
    // Arrange
    mockedApi.assignSystemRole.mockResolvedValueOnce({
      status: 'error',
      error: {
        code: 'RBAC_ROLE_ASSIGN_DENIED',
        message: 'Callers with effective role Isso may not assign AuthorizingOfficial.',
        callerEffectiveRole: 'Isso',
        targetRole: 'AuthorizingOfficial',
      },
    });

    render(
      <AssignRoleDialog
        open
        onClose={vi.fn()}
        scope={{ kind: 'system', registeredSystemId: 'sys-1' }}
        initialRole="AuthorizingOfficial"
        callerEffectiveRole="Isso"
        onAssigned={vi.fn()}
      />,
    );

    fireEvent.change(screen.getByLabelText(/person/i), {
      target: { value: '33333333-3333-3333-3333-333333333333' },
    });
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /assign/i }));
    });

    // Assert — error code surfaced
    await waitFor(() => {
      expect(screen.getByText(/may not assign/i)).toBeInTheDocument();
    });
  });
});
