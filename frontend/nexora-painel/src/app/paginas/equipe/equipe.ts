import { Component, ElementRef, OnInit, ViewChild, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe } from '@angular/common';
import {
  POR_PAGINA, Paginacao, alturaMinimaDaTabela, rolarParaTopoDaTabela
} from '../../nucleo/paginacao/paginacao';
import { EquipeServico } from '../../nucleo/servicos/equipe.servico';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { ToastServico } from '../../nucleo/toast/toast.servico';
import { PapelUsuario, Permissao, StatusUsuario, UsuarioEquipe } from '../../nucleo/modelos';
import { GESTOS_DELEGAVEIS } from '../../nucleo/seguranca/gestos';
import { iniciais } from '../../nucleo/iniciais';

/** A equipe da empresa: convidar por link, editar papel, ativar/inativar.
 *
 *  O enforcement é da API (403 por papel); aqui a tela é só do dono e esconde o que ele não
 *  pode fazer. Removida a comissão do atendente, que é de cobrança. */
@Component({
  selector: 'app-equipe',
  imports: [FormsModule, DatePipe, Paginacao],
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

  /** ===================== A PÁGINA VEM DO SERVIDOR (AUD-XX, #21) =====================
   *  `GET /api/equipe/pagina` devolve uma página com o total e as páginas prontos. A tela
   *  recortava a equipe inteira e dividia o tamanho dela por 20 — o recorte que o DES-1 já dizia
   *  que precisava subir para o servidor. A equipe inteira continua em `GET /api/equipe`, para os
   *  seletores de responsável das outras telas.
   *  ================================================================================ */
  pagina = signal(1);
  totalPaginas = signal(1);
  totalPessoas = signal(0);

  @ViewChild('tabelaTopo') private tabelaTopo?: ElementRef<HTMLElement>;

  alturaMinima = computed(() => this.totalPaginas() > 1 ? alturaMinimaDaTabela() : 0);

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

  // ---------------------------------------------------------------- permissões por pessoa (PER-1)
  /** O que os interruptores mostram: a lista EFETIVA desta pessoa. */
  edPermissoes = signal<Permissao[]>([]);

  protected readonly gestos = GESTOS_DELEGAVEIS;

  /** ===================== O SELETOR DE PAPEL TRAVA OS INTERRUPTORES =====================
   *  Trocou o papel? A base muda, e as marcações atuais deixam de significar o que significavam.
   *
   *  ⚠️ O BUG QUE ISSO EVITA: o dono abre um VENDEDOR (tudo desligado, porque vendedor não pode
   *  nada), troca o seletor para Gestor e salva. Sem travar, a tela mandaria dez desmarcados com
   *  papel=gestor — e seriam dez NEGAÇÕES gravadas. "Promovi para gestor e ele continua sem ver
   *  os números."
   *
   *  ⚠️ A TRAVA DE VERDADE ESTÁ NO SERVIDOR, que descarta a lista quando o papel muda. Esta aqui
   *  é só para a tela não prometer o que não vai acontecer: quem monta a requisição não decide a
   *  autorização.
   *  ====================================================================== */
  papelMudou = computed(() => this.edPapel() !== this.editando()?.papel);

  /** Dono pode tudo: não há o que ajustar, e tentar seria se trancar fora da própria conta. */
  edEhDono = computed(() => this.edPapel() === 'dono');

  /** Os interruptores só valem quando o papel salvo é o que está no seletor. */
  podeMexerNasPermissoes = computed(() => !this.edEhDono() && !this.papelMudou());

  temGesto(chave: Permissao) { return this.edPermissoes().includes(chave); }

  alternarGesto(chave: Permissao) {
    if (!this.podeMexerNasPermissoes()) return;
    this.edPermissoes.update(atual =>
      atual.includes(chave) ? atual.filter(p => p !== chave) : [...atual, chave]);
  }

  doGrupo(grupo: 'dia' | 'configuracao') {
    return this.gestos.filter(g => g.grupo === grupo);
  }

  ngOnInit() { this.carregar(); }

  carregar() {
    this.carregando.set(true);
    this.servico.pagina(this.pagina(), POR_PAGINA).subscribe({
      next: p => {
        // A página esvaziou (saiu a última linha dela): vai direto para a última que existe,
        // pelo `totalPaginas` do servidor (AUD-XX, #21).
        if (p.itens.length === 0 && p.totalCount > 0 && this.pagina() > p.totalPaginas) {
          this.pagina.set(p.totalPaginas);
          this.carregar();
          return;
        }

        this.usuarios.set(p.itens);
        this.totalPessoas.set(p.totalCount);
        this.totalPaginas.set(p.totalPaginas);
        this.carregando.set(false);
      },
      error: () => { this.erro.set('Não foi possível carregar a equipe.'); this.carregando.set(false); }
    });
  }

  irPara(p: number) {
    this.pagina.set(p);
    this.carregar();
    rolarParaTopoDaTabela(this.tabelaTopo?.nativeElement);
  }

  ehEu(u: UsuarioEquipe) { return u.id === this.meuId; }

  /** Uma copia so, em `nucleo/iniciais.ts` — o avatar e a MESMA coisa em toda tela. Eram seis
   *  copias, e as de contato mostravam "(9" para quem nasceu com o telefone por nome. */
  protected readonly iniciais = iniciais;

  /** "Rafael Lima" -> "o Rafael". O nome inteiro no meio da frase fica formal demais para uma
   *  tela em que o dono está decidindo sobre gente que ele conhece. */
  primeiroNome(nome: string): string {
    return (nome ?? '').trim().split(/\s+/)[0] || 'esta pessoa';
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
    // Os interruptores nascem com o que a pessoa pode HOJE — o papel dela ± o que já foi
    // ajustado. O servidor manda o efetivo pronto; a tela não deduz do papel.
    this.edPermissoes.set([...u.permissoes]);
    this.erroEdit.set('');
  }

  fecharEdicao() { this.editando.set(null); }

  salvarEdicao() {
    const u = this.editando();
    if (!u) return;
    this.salvandoEdit.set(true);
    this.erroEdit.set('');
    // ⚠️ A LISTA SÓ VAI QUANDO FAZ SENTIDO. Com o papel trocado, a base muda e o servidor
    // descarta a lista de qualquer jeito; para um dono, ele RECUSA. Mandar `undefined` nos dois
    // casos é o que faz a tela pedir exatamente o que ela promete na nota ao lado.
    const permissoes = this.podeMexerNasPermissoes() ? this.edPermissoes() : undefined;

    this.servico.atualizar(
      u.id, this.edNome(), this.edPapel(), this.edStatus(), permissoes).subscribe({
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
