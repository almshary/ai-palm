"""Reserve a job for one explicitly selected device.

Revision ID: 0002_target_host
Revises: 0001_initial
"""
from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa


revision: str = "0002_target_host"
down_revision: Union[str, None] = "0001_initial"
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.add_column("jobs", sa.Column("target_host_id", sa.String(length=64), nullable=True))
    op.create_foreign_key(
        "fk_jobs_target_host_id_devices",
        "jobs",
        "devices",
        ["target_host_id"],
        ["id"],
        ondelete="SET NULL",
    )
    op.create_index("ix_jobs_target_status", "jobs", ["target_host_id", "status"], unique=False)


def downgrade() -> None:
    op.drop_index("ix_jobs_target_status", table_name="jobs")
    op.drop_constraint("fk_jobs_target_host_id_devices", "jobs", type_="foreignkey")
    op.drop_column("jobs", "target_host_id")
