import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { EquipeServico } from '../../nucleo/servicos/equipe.servico';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { ToastServico } from '../../nucleo/toast/toast.servico';
import { PapelUsuario, StatusUsuario, UsuarioEquipe } from '../../nucleo/modelos';

/** A equipe da empresa: convidar por link, editar papel, ativar/inativar.
 *
 *  O enforcement é da API (403 por papel); aqui a tela é só do dono e esconde o que ele não
 *  pode fazer. Removida a comissão do atendente, que é de cobrança. */
@Component({
  selector: 'app-equipe',
  imports: [FormsModule, DatePipe],
  templateUrl: './equipe.html',
  styleUrl: './equipe.css'
})
export class Equipe implements OnInit {
  private servico = inject(EquipeServico);
  private auth = inject(AuthServico);
  private toast = inject(ToastServico);

  usuarios = signal<UsuarioEquipe[]>([]);
  carregando = signal(true);
  erro = signal('');

  meuId = this.auth.usuario()?.id ?? 0;

  // convite
  modalConvite = signal(false);
  cNome = signal('');
  cEmail = signal('');
  cPapel = signal<PapelUsuario>('vendedor');
  salvandoConvite = signal(false);
  erroConvite = signal('');

  /** O link gerado. NÃO há envio de e-mail na fase 1 — o dono copia e manda por fora.
   *  Limitação registrada desde o bloco 1. */
  linkGerado = signal('');
  linkEhReset = signal(false);
  copiado = signal(false);

  // edição
  editando = signal<UsuarioEquipe | null>(null);
  edNome = signal('');
  edPapel = signal<PapelUsuario>('vendedor');
  edStatus = signal<'ativo' | 'inativo'>('ativo');
  salvandoEdit = signal(false);
  erroEdit = signal('');

  ngOnInit() { this.carregar(); }

  carregar() {
    this.carregando.set(true);
    this.servico.listar().subscribe({
      next: us => { this.usuarios.set(us); this.carregando.set(false); },
      error: () => { this.erro.set('Não foi possível carregar a equipe.'); this.carregando.set(false); }
    });
  }

  ehEu(u: UsuarioEquipe) { return u.id === this.meuId; }

  iniciais(nome: string): string {
    const p = (nome || '').trim().split(/\s+/);
    return ((p[0]?.[0] ?? '') + (p.length > 1 ? p[p.length - 1][0] : '')).toUpperCase() || '?';
  }

  rotuloPapel(p: PapelUsuario): string {
    return p === 'dono' ? 'Dono' : p === 'gestor' ? 'Gestor' : 'Vendedor';
  }

  // ---------------------------------------------------------------- convite
  abrirConvite() {
    this.cNome.set(''); this.cEmail.set(''); this.cPapel.set('vendedor');
    this.erroConvite.set(''); this.linkGerado.set(''); this.copiado.set(false);
    this.linkEhReset.set(false);
    this.modalConvite.set(true);
  }

  fecharConvite() { this.modalConvite.set(false); this.carregar(); }

  convidar() {
    this.salvandoConvite.set(true);
    this.erroConvite.set('');
    this.servico.convidar(this.cNome(), this.cEmail(), this.cPapel()).subscribe({
      next: r => {
        this.salvandoConvite.set(false);
        this.linkGerado.set(`${window.location.origin}/convite/${r.token}`);
      },
      error: e => {
        this.erroConvite.set(e.error?.erro ?? 'Não foi possível convidar.');
        this.salvandoConvite.set(false);
      }
    });
  }

  reenviar(u: UsuarioEquipe) {
    this.servico.reenviarConvite(u.id).subscribe({
      next: r => {
        this.linkGerado.set(`${window.location.origin}/convite/${r.token}`);
        this.linkEhReset.set(false); this.copiado.set(false); this.modalConvite.set(true);
      },
      error: e => this.toast.erro(e.error?.erro ?? 'Não foi possível reenviar.')
    });
  }

  resetarSenha(u: UsuarioEquipe) {
    if (!confirm(`Gerar link de redefinição de senha para ${u.nome}? O link anterior deixa de valer.`)) return;
    this.servico.gerarResetSenha(u.id).subscribe({
      next: r => {
        this.linkGerado.set(`${window.location.origin}/redefinir/${r.token}`);
        this.linkEhReset.set(true); this.copiado.set(false); this.modalConvite.set(true);
      },
      error: e => this.toast.erro(e.error?.erro ?? 'Não foi possível gerar o link.')
    });
  }

  copiar() {
    navigator.clipboard?.writeText(this.linkGerado()).then(() => this.copiado.set(true));
  }

  // ---------------------------------------------------------------- edição
  abrirEdicao(u: UsuarioEquipe) {
    this.editando.set(u);
    this.edNome.set(u.nome);
    this.edPapel.set(u.papel);
    this.edStatus.set(u.status === 'inativo' ? 'inativo' : 'ativo');
    this.erroEdit.set('');
  }

  fecharEdicao() { this.editando.set(null); }

  salvarEdicao() {
    const u = this.editando();
    if (!u) return;
    this.salvandoEdit.set(true);
    this.erroEdit.set('');
    this.servico.atualizar(u.id, this.edNome(), this.edPapel(), this.edStatus()).subscribe({
      next: () => { this.salvandoEdit.set(false); this.editando.set(null); this.carregar(); },
      error: e => {
        this.erroEdit.set(e.error?.erro ?? 'Não foi possível salvar.');
        this.salvandoEdit.set(false);
      }
    });
  }

  mudarStatus(u: UsuarioEquipe, status: StatusUsuario) {
    if (status === 'inativo' && !confirm(`Inativar ${u.nome}?`)) return;
    this.servico.atualizar(u.id, u.nome, u.papel, status).subscribe({
      next: () => this.carregar(),
      error: e => this.toast.erro(e.error?.erro ?? 'Não foi possível alterar.')
    });
  }
}
